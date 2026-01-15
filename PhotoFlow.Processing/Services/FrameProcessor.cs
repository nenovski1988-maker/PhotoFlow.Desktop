using PhotoFlow.Core.Domain;
using PhotoFlow.Core.Services;
using PhotoFlow.Licensing.Services;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFlow.Processing.Services;

public sealed class FrameProcessor : IFrameProcessor
{
    private readonly IWorkspaceLayout _workspace;
    private readonly ILicensingService _licensing;

    // Lazy-load AI models (one per app)
    private static readonly object _aiLock = new();
    private static ModNetMatting? _modnet;
    private static U2NetMatting? _u2net;

    // Trial watermark settings
    private const string TrialWatermarkText = "TRIAL VERSION";
    private const int TrialWatermarkOpacityPercent = 4; // "opacity 4" => 4%

    public FrameProcessor(IWorkspaceLayout workspace, ILicensingService licensing)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
    }

    public async Task<IReadOnlyList<string>> ProcessAsync(
        ProductSession session,
        Frame frame,
        ProcessingOptions options,
        CancellationToken ct = default)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (frame is null) throw new ArgumentNullException(nameof(frame));

        var barcode = session.Barcode;

        Directory.CreateDirectory(_workspace.GetProcessedFolder(barcode));
        Directory.CreateDirectory(_workspace.GetExportsFolder(barcode));

        // Entitlements (еднократно)
        var entitlements = _licensing.GetEntitlements();
        var aiAllowed = entitlements.AiBackgroundRemovalAllowed;
        var watermarkRequired = entitlements.WatermarkRequired;

        using var img = await Image.LoadAsync<Rgba32>(frame.RawPath, ct);

        ApplyBackground(img, options, aiAllowed);

        // optional: cleanup white ground shadows after AI
        if ((options.BackgroundMethod == BackgroundMethod.AiModNetFast ||
             options.BackgroundMethod == BackgroundMethod.AiU2NetQuality) &&
            options.SuppressGroundShadow)
        {
            SuppressBottomWhiteShadow(img, options.ShadowWhiteThreshold, options.ShadowMaxAlpha, options.ShadowBottomPercent);
        }

        using var squared = MakeSquareCentered(img, options.SquareSize, options.PaddingPercent);

        // bounds на обекта върху прозрачния square (за watermark area + PureWhite)
        var opaqueBounds = FindOpaqueBounds(squared);

        // маска на обекта (по алфа) — критично за white exports (иначе watermark-а ще стане върху целия кадър)
        using var objectMask = BuildAlphaMask(squared);

        using Image<Rgba32> finalMaster = options.WhiteBackground
            ? CompositeOnWhite(squared)
            : squared.Clone();

        if (options.WhiteBackground && options.ForcePureWhiteBackground)
            ForcePureWhiteOutsideBounds(finalMaster, opaqueBounds);

        // Master винаги е чист (без watermark)
        var processedFolder = _workspace.GetProcessedFolder(barcode);
        var masterPath = Path.Combine(
            processedFolder,
            Path.GetFileNameWithoutExtension(frame.RawPath) + "_master.png");

        await finalMaster.SaveAsync(
            masterPath,
            new PngEncoder { CompressionLevel = PngCompressionLevel.DefaultCompression },
            ct);

        var outputs = new List<string> { masterPath };

        if (options.Exports != null)
        {
            foreach (var preset in options.Exports)
            {
                ct.ThrowIfCancellationRequested();

                var exportFolder = Path.Combine(_workspace.GetExportsFolder(barcode), SanitizeFolderName(preset.Name));
                Directory.CreateDirectory(exportFolder);

                using var resized = finalMaster.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(preset.Width, preset.Height),
                    Mode = ResizeMode.Max
                }));

                // Watermark само на export-ите (не на master)
                if (watermarkRequired)
                {
                    // resize на маската със същия resize като изображението
                    using var resizedMask = objectMask.Clone(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(preset.Width, preset.Height),
                        Mode = ResizeMode.Max
                    }));

                    var scaledBounds = ScaleBounds(opaqueBounds, finalMaster.Width, finalMaster.Height, resized.Width, resized.Height);

                    ApplyTrialWatermarkOverObject(
                        resized,
                        resizedMask,
                        scaledBounds,
                        TrialWatermarkText,
                        TrialWatermarkOpacityPercent);
                }

                var ext = preset.Format switch
                {
                    ExportImageFormat.Jpeg => ".jpg",
                    ExportImageFormat.Png => ".png",
                    ExportImageFormat.Webp => ".webp",
                    _ => ".jpg"
                };

                var exportPath = Path.Combine(
                    exportFolder,
                    $"{Path.GetFileNameWithoutExtension(frame.RawPath)}_{preset.Width}x{preset.Height}{ext}");

                switch (preset.Format)
                {
                    case ExportImageFormat.Jpeg:
                        await resized.SaveAsync(exportPath, new JpegEncoder { Quality = preset.Quality }, ct);
                        break;

                    case ExportImageFormat.Png:
                        await resized.SaveAsync(exportPath, new PngEncoder { CompressionLevel = PngCompressionLevel.DefaultCompression }, ct);
                        break;

                    case ExportImageFormat.Webp:
                        await resized.SaveAsync(exportPath, new WebpEncoder { Quality = preset.Quality }, ct);
                        break;
                }

                outputs.Add(exportPath);
            }
        }

        return outputs;
    }

    private void ApplyBackground(Image<Rgba32> img, ProcessingOptions options, bool aiAllowed)
    {
        byte threshold = options.WhiteThreshold;
        byte feather = options.Feather;

        if (options.BackgroundMethod == BackgroundMethod.AggressiveOverexposedToTransparent)
        {
            threshold = (byte)Math.Max(0, threshold - 10);
            feather = (byte)Math.Min(80, feather + 6);
        }

        // ако AI не е позволен -> forced fallback към "simple"
        if (!aiAllowed &&
            (options.BackgroundMethod == BackgroundMethod.AiModNetFast ||
             options.BackgroundMethod == BackgroundMethod.AiU2NetQuality))
        {
            RemoveNearWhiteSoft(img, threshold, feather);
            return;
        }

        switch (options.BackgroundMethod)
        {
            case BackgroundMethod.SimpleOverexposedToTransparent:
            case BackgroundMethod.AggressiveOverexposedToTransparent:
                RemoveNearWhiteSoft(img, threshold, feather);
                break;

            case BackgroundMethod.AiModNetFast:
                if (!TryApplyModNet(img, feather))
                    RemoveNearWhiteSoft(img, threshold, feather); // fallback
                break;

            case BackgroundMethod.AiU2NetQuality:
                if (!TryApplyU2Net(img, feather))
                    RemoveNearWhiteSoft(img, threshold, feather); // fallback
                break;
        }
    }

    private bool TryApplyModNet(Image<Rgba32> img, byte feather)
    {
        try
        {
            EnsureAiLoaded();
            if (_modnet == null) return false;
            _modnet.ApplyMatte(img, feather);
            return true;
        }
        catch { return false; }
    }

    private bool TryApplyU2Net(Image<Rgba32> img, byte feather)
    {
        try
        {
            EnsureAiLoaded();
            if (_u2net == null) return false;
            _u2net.ApplyMatte(img, feather);
            return true;
        }
        catch { return false; }
    }

    private void EnsureAiLoaded()
    {
        lock (_aiLock)
        {
            if (_modnet != null && _u2net != null) return;

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var modelsDir = Path.Combine(docs, "PhotoFlow", "models");
            Directory.CreateDirectory(modelsDir);

            var modnetPath = Path.Combine(modelsDir, "modnet.onnx");
            var u2netPath = Path.Combine(modelsDir, "u2net.onnx");

            if (_modnet == null && File.Exists(modnetPath))
                _modnet = new ModNetMatting(modnetPath);

            if (_u2net == null && File.Exists(u2netPath))
                _u2net = new U2NetMatting(u2netPath);
        }
    }

    // ===== WATERMARK helpers (mask-based, repeats over object) =====

    private static Image<L8> BuildAlphaMask(Image<Rgba32> src)
    {
        var mask = new Image<L8>(src.Width, src.Height);

        // Само ЕДИН ProcessPixelRows (без nested lambdas)
        mask.ProcessPixelRows(maskAcc =>
        {
            for (int y = 0; y < src.Height; y++)
            {
                var mRow = maskAcc.GetRowSpan(y);
                for (int x = 0; x < src.Width; x++)
                {
                    mRow[x] = new L8(src[x, y].A);
                }
            }
        });

        return mask;
    }



    private static void ApplyTrialWatermarkOverObject(
        Image<Rgba32> img,
        Image<L8> objectAlphaMask,
        Rectangle objectBounds,
        string text,
        int opacityPercent)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        opacityPercent = Math.Clamp(opacityPercent, 1, 100);
        var alpha = (byte)Math.Round(255f * (opacityPercent / 100f));

        if (objectBounds.Width <= 0 || objectBounds.Height <= 0)
            objectBounds = new Rectangle(0, 0, img.Width, img.Height);

        objectBounds = ExpandAndClamp(objectBounds, img.Width, img.Height, expandPx: Math.Max(6, img.Width / 80));

        using var overlay = new Image<Rgba32>(img.Width, img.Height, new Rgba32(0, 0, 0, 0));

        var minDim = MathF.Min(objectBounds.Width, objectBounds.Height); // НЕ ползвай "base" (keyword)
        var fontSize = MathF.Max(26, minDim / 6.5f);

        Font font;
        try { font = SystemFonts.CreateFont("Segoe UI", fontSize, FontStyle.Bold); }
        catch { font = SystemFonts.Collection.Families.First().CreateFont(fontSize, FontStyle.Bold); }

        var textOptions = new TextOptions(font)
        {
            Origin = new PointF(0, 0),
            Dpi = 96,
            KerningMode = KerningMode.Standard
        };

        var textSize = TextMeasurer.MeasureSize(text, textOptions);

        var stepX = MathF.Max(textSize.Width * 1.25f, 140);
        var stepY = MathF.Max(textSize.Height * 1.65f, 90);

        var cWhite = Color.FromRgba(255, 255, 255, alpha);
        var cBlack = Color.FromRgba(0, 0, 0, alpha);

        overlay.Mutate(ctx =>
        {
            int row = 0;
            for (float y = objectBounds.Top - stepY; y <= objectBounds.Bottom + stepY; y += stepY, row++)
            {
                float xOffset = (row % 2 == 0) ? 0 : stepX / 2f;

                for (float x = objectBounds.Left - stepX; x <= objectBounds.Right + stepX; x += stepX)
                {
                    var px = x + xOffset;
                    var py = y;

                    ctx.DrawText(text, font, cBlack, new PointF(px + 2, py + 2));
                    ctx.DrawText(text, font, cWhite, new PointF(px, py));
                }
            }
        });

        // Маскиране: overlay да остане само върху обекта (по маска) + само в bounds
        // Маскиране: overlay да остане само върху обекта (по маска) + само в bounds
        // Маскиране: overlay да остане само върху обекта (по маска) + само в bounds
        overlay.ProcessPixelRows(ovAcc =>
        {
            for (int y = 0; y < img.Height; y++)
            {
                var oRow = ovAcc.GetRowSpan(y);

                bool yInside = y >= objectBounds.Top && y < objectBounds.Bottom;

                for (int x = 0; x < img.Width; x++)
                {
                    var p = oRow[x];
                    if (p.A == 0) continue;

                    bool inside = yInside && x >= objectBounds.Left && x < objectBounds.Right;
                    if (!inside)
                    {
                        oRow[x].A = 0;
                        continue;
                    }

                    // Четем маската през индексера (без втори accessor)
                    byte maskA = objectAlphaMask[x, y].PackedValue; // 0..255
                    if (maskA < 10)
                    {
                        oRow[x].A = 0;
                        continue;
                    }

                    oRow[x].A = (byte)((p.A * maskA) / 255);
                }
            }
        });



        img.Mutate(ctx => ctx.DrawImage(overlay, new Point(0, 0), 1f));
    }

    private static Rectangle ScaleBounds(Rectangle b, int srcW, int srcH, int dstW, int dstH)
    {
        if (b.Width <= 0 || b.Height <= 0) return Rectangle.Empty;

        float sx = dstW / (float)srcW;
        float sy = dstH / (float)srcH;

        int x = (int)MathF.Round(b.X * sx);
        int y = (int)MathF.Round(b.Y * sy);
        int w = (int)MathF.Round(b.Width * sx);
        int h = (int)MathF.Round(b.Height * sy);

        return new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static Rectangle ExpandAndClamp(Rectangle r, int maxW, int maxH, int expandPx)
    {
        int x = Math.Max(0, r.X - expandPx);
        int y = Math.Max(0, r.Y - expandPx);
        int right = Math.Min(maxW, r.Right + expandPx);
        int bottom = Math.Min(maxH, r.Bottom + expandPx);
        return new Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    // ===== Existing processing helpers =====

    private static void SuppressBottomWhiteShadow(Image<Rgba32> img, byte whiteThr, byte maxAlpha, byte bottomPercent)
    {
        var bounds = FindOpaqueBounds(img);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        int percent = Math.Clamp(bottomPercent, (byte)5, (byte)60);
        int startY = bounds.Bottom - (bounds.Height * percent / 100);
        if (startY < bounds.Top) startY = bounds.Top;

        int left = bounds.Left;
        int right = bounds.Right;
        int top = startY;
        int bottom = bounds.Bottom;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = top; y < bottom; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = left; x < right; x++)
                {
                    var p = row[x];
                    if (p.A == 0) continue;
                    if (p.A > maxAlpha) continue;

                    if (p.R >= whiteThr && p.G >= whiteThr && p.B >= whiteThr)
                        row[x] = new Rgba32(p.R, p.G, p.B, 0);
                }
            }
        });
    }

    private static void RemoveNearWhiteSoft(Image<Rgba32> img, byte threshold, byte feather)
    {
        int t = threshold;
        int f = Math.Clamp(feather, (byte)0, (byte)80);

        int tLow = Math.Max(0, t - f);
        int tHigh = t;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];

                    int w = Math.Min(p.R, Math.Min(p.G, p.B));

                    if (w >= tHigh)
                    {
                        row[x] = new Rgba32(p.R, p.G, p.B, 0);
                    }
                    else if (f > 0 && w >= tLow)
                    {
                        float alphaFactor = (float)(tHigh - w) / (tHigh - tLow);
                        alphaFactor = Math.Clamp(alphaFactor, 0f, 1f);
                        byte newA = (byte)Math.Round(p.A * alphaFactor);
                        row[x] = new Rgba32(p.R, p.G, p.B, newA);
                    }
                }
            }
        });
    }

    private static Image<Rgba32> MakeSquareCentered(Image<Rgba32> img, int squareSize, float paddingPercent)
    {
        var bounds = FindOpaqueBounds(img);

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new Image<Rgba32>(squareSize, squareSize, new Rgba32(255, 255, 255, 255));

        using var cropped = img.Clone(x => x.Crop(bounds));

        var target = (int)(squareSize * (1.0f - Math.Clamp(paddingPercent, 0f, 0.45f)));
        if (target < 200) target = 200;

        var scale = Math.Min((float)target / cropped.Width, (float)target / cropped.Height);
        var newW = Math.Max(1, (int)(cropped.Width * scale));
        var newH = Math.Max(1, (int)(cropped.Height * scale));

        cropped.Mutate(x => x.Resize(newW, newH));

        var canvas = new Image<Rgba32>(squareSize, squareSize, new Rgba32(0, 0, 0, 0));
        var offsetX = (squareSize - newW) / 2;
        var offsetY = (squareSize - newH) / 2;

        canvas.Mutate(x => x.DrawImage(cropped, new Point(offsetX, offsetY), 1f));
        return canvas;
    }

    private static Rectangle FindOpaqueBounds(Image<Rgba32> img)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x].A > 10)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        });

        if (maxX < 0 || maxY < 0) return Rectangle.Empty;
        return new Rectangle(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
    }

    private static Image<Rgba32> CompositeOnWhite(Image<Rgba32> transparent)
    {
        var white = new Image<Rgba32>(transparent.Width, transparent.Height, new Rgba32(255, 255, 255, 255));
        white.Mutate(x => x.DrawImage(transparent, new Point(0, 0), 1f));
        return white;
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static void ForcePureWhiteOutsideBounds(Image<Rgba32> img, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        row[x] = new Rgba32(255, 255, 255, 255);
                }
            });
            return;
        }

        int left = bounds.Left;
        int right = bounds.Right;
        int top = bounds.Top;
        int bottom = bounds.Bottom;

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                bool yInside = y >= top && y < bottom;

                for (int x = 0; x < row.Length; x++)
                {
                    bool inside = yInside && x >= left && x < right;
                    if (!inside)
                        row[x] = new Rgba32(255, 255, 255, 255);
                }
            }
        });
    }
}
