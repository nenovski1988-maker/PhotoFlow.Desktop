namespace PhotoFlow.Processing.Services;

public enum BackgroundMethod
{
    SimpleOverexposedToTransparent = 0,
    AggressiveOverexposedToTransparent = 1,

    // AI options (choose in Preview)
    AiModNetFast = 2,
    AiU2NetQuality = 3,
        
}

public enum ExportImageFormat
{
    Jpeg = 0,
    Png = 1,
    Webp = 2
}

public sealed record ExportPreset(
    string Name,
    int Width,
    int Height,
    ExportImageFormat Format,
    int Quality = 90
);

public sealed record ProcessingOptions(
    BackgroundMethod BackgroundMethod,
    int SquareSize,
    float PaddingPercent,
    bool WhiteBackground,

    // classic (near-white)
    byte WhiteThreshold,
    byte Feather,

    // AI cleanup helpers
    bool SuppressGroundShadow = true,
    byte ShadowWhiteThreshold = 245,
    byte ShadowMaxAlpha = 120,
    byte ShadowBottomPercent = 30,
    bool ForcePureWhiteBackground = false,

    bool ApplyWatermark = false,            // <-- ДОБАВИ ТОВА

    IReadOnlyList<ExportPreset>? Exports = null
);

