using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoFlow.Processing.Services;

/// <summary>
/// U^2-Net matting (rembg-like).
/// IMPORTANT: Many U2Net ONNX exports output multiple maps (d0..d6).
/// We must pick d0 (best) or the most plausible 1x1xHxW.
/// </summary>
public sealed class U2NetMatting : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    // most exported u2net models are fixed 320 or 512; we will infer if possible
    private int _inW = 320;
    private int _inH = 320;

    // rembg-style normalization
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    public U2NetMatting(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is empty.", nameof(modelPath));

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("U2Net model not found.", modelPath);

        var opts = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelPath, opts);

        var inMeta = _session.InputMetadata.First();
        _inputName = inMeta.Key;

        var dims = inMeta.Value.Dimensions;
        // expected NCHW
        if (dims != null && dims.Length >= 4)
        {
            if (dims[2] > 0) _inH = dims[2];
            if (dims[3] > 0) _inW = dims[3];
        }

        if (_inH < 64) _inH = 320;
        if (_inW < 64) _inW = 320;
    }

    public void Dispose() => _session.Dispose();

    public void ApplyMatte(Image<Rgba32> img, byte feather)
    {
        if (img is null) throw new ArgumentNullException(nameof(img));

        using var resized = img.Clone(x => x.Resize(_inW, _inH));

        var input = new DenseTensor<float>(new[] { 1, 3, _inH, _inW });

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < _inH; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < _inW; x++)
                {
                    var p = row[x];

                    float r = (p.R / 255f - Mean[0]) / Std[0];
                    float g = (p.G / 255f - Mean[1]) / Std[1];
                    float b = (p.B / 255f - Mean[2]) / Std[2];

                    input[0, 0, y, x] = r;
                    input[0, 1, y, x] = g;
                    input[0, 2, y, x] = b;
                }
            }
        });

        var inputNv = NamedOnnxValue.CreateFromTensor(_inputName, input);
        using var results = _session.Run(new[] { inputNv });

        // Pick best output:
        // - prefer "d0"
        // - else prefer name containing "mask"/"alpha"/"output"
        // - else prefer 4D tensor
        var outTensor = PickBestU2NetOutput(results);

        // Read raw -> optional sigmoid -> minmax normalize -> contrast -> bytes
        var raw = new float[_inW * _inH];
        float min = float.MaxValue, max = float.MinValue;

        for (int y = 0; y < _inH; y++)
        {
            int ro = y * _inW;
            for (int x = 0; x < _inW; x++)
            {
                float a = ReadOutput(outTensor, x, y);
                raw[ro + x] = a;
                if (a < min) min = a;
                if (a > max) max = a;
            }
        }

        // If logits -> sigmoid. (very common in U2Net exports)
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

        // minmax normalize to avoid "whole image semi transparent"
        float range = Math.Max(1e-6f, max - min);
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (raw[i] - min) / range;

        // Contrast curve (tighten edges / push bg to 0)
        const float lo = 0.18f;
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
            float radius = Math.Clamp(feather / 10f, 0f, 8f);
            if (radius > 0.01f)
                matteSmallImg.Mutate(x => x.GaussianBlur(radius));
        }

        using var matteImg = matteSmallImg.Clone(x => x.Resize(img.Width, img.Height));

        // extract alpha
        var matteA = new byte[img.Width * img.Height];
        matteImg.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int ro = y * accessor.Width;
                for (int x = 0; x < row.Length; x++)
                    matteA[ro + x] = row[x].R;
            }
        });
        // Snap edges: sharper contour
        EdgeSnapInPlace(matteA, lo: 55, hi: 205, gamma: 0.75f);

        // apply alpha + light dehalo (assume white bg)
        const float dehaloStrength = 0.85f;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int ro = y * accessor.Width;

                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    byte mA = matteA[ro + x];

                    if (mA == 0)
                    {
                        row[x] = new Rgba32(p.R, p.G, p.B, 0);
                        continue;
                    }

                    float aN = (p.A / 255f) * (mA / 255f);
                    byte newA = (byte)Math.Clamp((int)Math.Round(aN * 255f), 0, 255);

                    if (newA > 0 && newA < 255)
                    {
                        float inv = (1f - (newA / 255f)) * 255f;
                        float denom = Math.Max(0.001f, newA / 255f);

                        float r = (p.R - inv) / denom;
                        float g = (p.G - inv) / denom;
                        float b = (p.B - inv) / denom;

                        r = Math.Clamp(r, 0f, 255f);
                        g = Math.Clamp(g, 0f, 255f);
                        b = Math.Clamp(b, 0f, 255f);

                        byte nr = (byte)Math.Round(p.R * (1f - dehaloStrength) + r * dehaloStrength);
                        byte ng = (byte)Math.Round(p.G * (1f - dehaloStrength) + g * dehaloStrength);
                        byte nb = (byte)Math.Round(p.B * (1f - dehaloStrength) + b * dehaloStrength);

                        row[x] = new Rgba32(nr, ng, nb, newA);
                    }
                    else
                    {
                        row[x] = new Rgba32(p.R, p.G, p.B, newA);
                    }
                }
            }
        });
    }

    private static Tensor<float> PickBestU2NetOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        // 1) Prefer "d0"
        var d0 = results.FirstOrDefault(v => string.Equals(v.Name, "d0", StringComparison.OrdinalIgnoreCase));
        if (d0 != null) return d0.AsTensor<float>();

        // 2) Prefer something that looks like mask/alpha
        var preferred = results.FirstOrDefault(v =>
            v.Name.Contains("mask", StringComparison.OrdinalIgnoreCase) ||
            v.Name.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
            v.Name.Contains("output", StringComparison.OrdinalIgnoreCase));

        if (preferred != null) return preferred.AsTensor<float>();

        // 3) Prefer 4D tensors
        foreach (var v in results)
        {
            var t = v.AsTensor<float>();
            if (t.Rank == 4) return t;
        }

        // fallback
        return results.First().AsTensor<float>();
    }

    private static float ReadOutput(Tensor<float> output, int x, int y)
    {
        // common shapes:
        // [1,1,H,W] or [1,H,W] or [H,W]
        if (output.Rank == 4) return output[0, 0, y, x];
        if (output.Rank == 3) return output[0, y, x];
        if (output.Rank == 2) return output[y, x];
        return 0f;
    }

    private static float Sigmoid(float x)
        => 1f / (1f + (float)Math.Exp(-x));

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

        KeepLargestComponentInPlace(bin, w, h);
        FillHolesInPlace(bin, w, h);

        for (int i = 0; i < tightenIters; i++)
            ErodeInPlace(bin, w, h);

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

        if (bestSeed < 0)
        {
            Array.Clear(mask, 0, mask.Length);
            return;
        }

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

        for (int i = 0; i < mask.Length; i++)
            mask[i] = visited[i];
    }

    private static void FillHolesInPlace(bool[] mask, int w, int h)
    {
        var visited = new bool[mask.Length];
        int[] q = new int[mask.Length];
        int qh = 0, qt = 0;

        void EnqueueIfBg(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int idx = y * w + x;
            if (visited[idx]) return;
            if (mask[idx]) return;
            visited[idx] = true;
            q[qt++] = idx;
        }

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
