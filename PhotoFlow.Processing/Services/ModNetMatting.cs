using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoFlow.Processing.Services;

public sealed class ModNetMatting : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    private int _inW;
    private int _inH;

    public ModNetMatting(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is empty.", nameof(modelPath));

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model not found.", modelPath);

        var opts = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelPath, opts);

        var inMeta = _session.InputMetadata.First();
        _inputName = inMeta.Key;

        // Input dims usually: [1,3,H,W] but some are dynamic (-1 / 0)
        var dims = inMeta.Value.Dimensions;
        _inH = (dims != null && dims.Length >= 3 && dims[2] > 0) ? dims[2] : 512;
        _inW = (dims != null && dims.Length >= 4 && dims[3] > 0) ? dims[3] : 512;

        if (_inH < 64) _inH = 512;
        if (_inW < 64) _inW = 512;
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// Applies matte to img alpha channel. Feather is used as soft edge blur (0..50 typical).
    /// </summary>
    public void ApplyMatte(Image<Rgba32> img, byte feather)
    {
        if (img is null) throw new ArgumentNullException(nameof(img));

        using var resized = img.Clone(x => x.Resize(_inW, _inH));

        var input = new DenseTensor<float>(new[] { 1, 3, _inH, _inW });

        // MODNet commonly expects [-1..1] normalization (NOT ImageNet mean/std)
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];

                    float r = (p.R / 255f - 0.5f) / 0.5f;
                    float g = (p.G / 255f - 0.5f) / 0.5f;
                    float b = (p.B / 255f - 0.5f) / 0.5f;

                    input[0, 0, y, x] = r;
                    input[0, 1, y, x] = g;
                    input[0, 2, y, x] = b;
                }
            }
        });

        var inputNv = NamedOnnxValue.CreateFromTensor(_inputName, input);
        using var results = _session.Run(new[] { inputNv });

        // MODNet exports vary: sometimes output name isn't the "alpha".
        // We pick the most plausible matte tensor from runtime results.
        var output = PickBestMatteTensor(results, _inW, _inH);

        // Read matte -> float[], normalize, contrast, postprocess
        var raw = new float[_inW * _inH];
        float min = float.MaxValue, max = float.MinValue;

        for (int y = 0; y < _inH; y++)
        {
            int off = y * _inW;
            for (int x = 0; x < _inW; x++)
            {
                float a = ReadOutput(output, x, y);
                raw[off + x] = a;
                if (a < min) min = a;
                if (a > max) max = a;
            }
        }

        // If logits/outside 0..1 -> sigmoid
        bool needSigmoid = (min < -0.05f) || (max > 1.05f);
        if (needSigmoid)
        {
            min = float.MaxValue; max = float.MinValue;
            for (int i = 0; i < raw.Length; i++)
            {
                float a = Sigmoid(raw[i]);
                raw[i] = a;
                if (a < min) min = a;
                if (a > max) max = a;
            }
        }

        // Min-max normalize (helps with "whole image semi-transparent")
        float range = Math.Max(1e-6f, max - min);
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (raw[i] - min) / range;

        // Contrast
        const float lo = 0.20f;
        const float hi = 0.90f;

        var matteSmallA = new byte[_inW * _inH];
        for (int i = 0; i < raw.Length; i++)
        {
            float a = SmoothStep(lo, hi, raw[i]);

            if (a < 0.02f) a = 0f;
            if (a > 0.98f) a = 1f;

            matteSmallA[i] = (byte)Math.Round(a * 255f);
        }

        // Postprocess: invert if needed, keep main object, fill holes, tighten edges
        PostProcessMatteInPlace(matteSmallA, _inW, _inH, tightenIters: 1);

        // To image -> blur -> resize
        using var matteSmallImg = ByteMaskToImage(matteSmallA, _inW, _inH);

        if (feather > 0)
        {
            float radius = Math.Clamp(feather / 8f, 0f, 6f);
            if (radius > 0.01f)
                matteSmallImg.Mutate(x => x.GaussianBlur(radius));
        }

        using var matteImg = matteSmallImg.Clone(x => x.Resize(img.Width, img.Height));

        // Extract alpha
        var matteA = new byte[img.Width * img.Height];
        matteImg.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int off = y * accessor.Width;
                for (int x = 0; x < row.Length; x++)
                    matteA[off + x] = row[x].R;
            }
        });
        EdgeSnapInPlace(matteA, lo: 55, hi: 205, gamma: 0.75f);

        // Apply alpha
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int off = y * accessor.Width;

                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    byte mA = matteA[off + x];
                    byte newA = (byte)Math.Round((p.A / 255f) * (mA / 255f) * 255f);
                    row[x] = new Rgba32(p.R, p.G, p.B, newA);
                }
            }
        });
    }

    private static Tensor<float> PickBestMatteTensor(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int w, int h)
    {
        // Prefer typical names
        var preferred = results.FirstOrDefault(v =>
            v.Name.Contains("pha", StringComparison.OrdinalIgnoreCase) ||
            v.Name.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
            v.Name.Contains("mask", StringComparison.OrdinalIgnoreCase));

        if (preferred != null)
            return preferred.AsTensor<float>();

        // Prefer a 4D tensor with [1,1,H,W]
        foreach (var v in results)
        {
            var t = v.AsTensor<float>();
            if (t.Rank == 4)
            {
                // try to match dims if available
                // Note: ORT dims can be unknown at runtime, so keep it simple.
                return t;
            }
        }

        // Fallback: first output
        return results.First().AsTensor<float>();
    }

    private static float ReadOutput(Tensor<float> output, int x, int y)
    {
        if (output.Rank == 4) return output[0, 0, y, x];
        if (output.Rank == 3) return output[0, y, x];
        if (output.Rank == 2) return output[y, x];
        return 0f;
    }

    private static float Sigmoid(float v)
        => 1f / (1f + MathF.Exp(-v));

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        x = Math.Clamp((x - edge0) / Math.Max(1e-6f, (edge1 - edge0)), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // =============================
    // Matte post-processing helpers
    // =============================

    private static Image<Rgba32> ByteMaskToImage(byte[] a, int w, int h)
    {
        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                int off = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte m = a[off + x];
                    row[x] = new Rgba32(m, m, m, 255);
                }
            }
        });
        return img;
    }

    private static void PostProcessMatteInPlace(byte[] matte, int w, int h, int tightenIters)
    {
        // Build binary mask from matte (keep faint edges too)
        const byte thr = 30;
        var bin = new bool[w * h];
        for (int i = 0; i < matte.Length; i++)
            bin[i] = matte[i] >= thr;

        // Auto-invert if corners look like foreground
        int c1 = matte[0];
        int c2 = matte[w - 1];
        int c3 = matte[(h - 1) * w];
        int c4 = matte[(h - 1) * w + (w - 1)];
        int avgCorner = (c1 + c2 + c3 + c4) / 4;

        if (avgCorner > 128)
        {
            for (int i = 0; i < bin.Length; i++)
                bin[i] = !bin[i];

            for (int i = 0; i < matte.Length; i++)
                matte[i] = (byte)(255 - matte[i]);
        }

        // Keep largest component (remove islands)
        KeepLargestComponentInPlace(bin, w, h);

        // Fill holes inside object
        FillHolesInPlace(bin, w, h);

        // Tighten edges to reduce glow
        for (int i = 0; i < tightenIters; i++)
            ErodeInPlace(bin, w, h);

        // Apply binary to matte (hard-remove background, keep original alpha in object)
        for (int i = 0; i < matte.Length; i++)
            if (!bin[i]) matte[i] = 0;
    }

    private static void KeepLargestComponentInPlace(bool[] mask, int w, int h)
    {
        var visited = new bool[mask.Length];
        int bestCount = 0;
        int bestSeed = -1;

        int[] q = new int[mask.Length];
        int qh, qt;

        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || visited[i]) continue;

            qh = 0; qt = 0;
            q[qt++] = i;
            visited[i] = true;

            int count = 0;

            while (qh < qt)
            {
                int idx = q[qh++];
                count++;

                int x = idx % w;
                int y = idx / w;

                void Try(int nx, int ny)
                {
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) return;
                    int n = ny * w + nx;
                    if (!mask[n] || visited[n]) return;
                    visited[n] = true;
                    q[qt++] = n;
                }

                Try(x - 1, y);
                Try(x + 1, y);
                Try(x, y - 1);
                Try(x, y + 1);
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestSeed = i;
            }
        }

        // If nothing found, clear
        if (bestSeed < 0)
        {
            Array.Clear(mask, 0, mask.Length);
            return;
        }

        // Re-run flood fill from bestSeed and keep only that component
        Array.Clear(visited, 0, visited.Length);

        qh = 0; qt = 0;
        q[qt++] = bestSeed;
        visited[bestSeed] = true;

        while (qh < qt)
        {
            int idx = q[qh++];
            int x = idx % w;
            int y = idx / w;

            void Try(int nx, int ny)
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) return;
                int n = ny * w + nx;
                if (!mask[n] || visited[n]) return;
                visited[n] = true;
                q[qt++] = n;
            }

            Try(x - 1, y);
            Try(x + 1, y);
            Try(x, y - 1);
            Try(x, y + 1);
        }

        // visited now is the kept component
        for (int i = 0; i < mask.Length; i++)
            mask[i] = visited[i];
    }

    private static void FillHolesInPlace(bool[] mask, int w, int h)
    {
        // Flood fill "background" from borders on inverted mask,
        // then holes are the false pixels not reached.
        var visited = new bool[mask.Length];
        int[] q = new int[mask.Length];
        int qh = 0, qt = 0;

        void EnqueueIfBg(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int idx = y * w + x;
            if (visited[idx]) return;
            if (mask[idx]) return; // object, not background
            visited[idx] = true;
            q[qt++] = idx;
        }

        // border pixels
        for (int x = 0; x < w; x++)
        {
            EnqueueIfBg(x, 0);
            EnqueueIfBg(x, h - 1);
        }
        for (int y = 0; y < h; y++)
        {
            EnqueueIfBg(0, y);
            EnqueueIfBg(w - 1, y);
        }

        while (qh < qt)
        {
            int idx = q[qh++];
            int x = idx % w;
            int y = idx / w;

            EnqueueIfBg(x - 1, y);
            EnqueueIfBg(x + 1, y);
            EnqueueIfBg(x, y - 1);
            EnqueueIfBg(x, y + 1);
        }

        // Any background pixel not visited is a hole -> fill it
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] && !visited[i])
                mask[i] = true;
        }
    }

    private static void ErodeInPlace(bool[] mask, int w, int h)
    {
        var src = (bool[])mask.Clone();

        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                if (!src[i]) { mask[i] = false; continue; }

                // 8-neighborhood
                bool ok =
                    src[i - 1] && src[i + 1] &&
                    src[i - w] && src[i + w] &&
                    src[i - w - 1] && src[i - w + 1] &&
                    src[i + w - 1] && src[i + w + 1];

                mask[i] = ok;
            }
        }
    }
    private static void EdgeSnapInPlace(byte[] alpha, byte lo, byte hi, float gamma)
    {
        // lo/hi са "levels" – под lo става 0, над hi става 255
        // gamma < 1.0 прави ръба по-остър (повече плътност около обекта)
        // gamma > 1.0 омекотява
        if (hi <= lo) hi = (byte)Math.Min(255, lo + 1);

        float invRange = 1f / (hi - lo);

        for (int i = 0; i < alpha.Length; i++)
        {
            float a = (alpha[i] - lo) * invRange; // 0..1 (rough)
            a = Math.Clamp(a, 0f, 1f);

            if (gamma != 1f)
                a = MathF.Pow(a, gamma);

            alpha[i] = (byte)Math.Round(a * 255f);
        }
    }

}
