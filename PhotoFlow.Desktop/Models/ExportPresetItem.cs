using PhotoFlow.Processing.Services;

namespace PhotoFlow.Desktop.Models;

public sealed class ExportPresetItem
{
    public required string DisplayName { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ExportImageFormat Format { get; init; }
    public int Quality { get; init; } = 90;

    // true = export върху бял фон; false = export прозрачно (PNG/WebP)
    public bool WhiteBackground { get; init; } = true;

    // NEW: ако е true -> за "Pure White" preset (заковава фона на #FFFFFF извън обекта)
    public bool ForcePureWhite { get; init; } = false;

    // NEW: ако е true -> за "White + Shadow" preset (не премахва долни бели сенки при AI)
    public bool KeepShadow { get; init; } = false;

    // checkbox
    public bool IsSelected { get; set; } = false;

    public ExportPreset ToProcessingPreset()
        => new ExportPreset(DisplayName, Width, Height, Format, Quality);
}
