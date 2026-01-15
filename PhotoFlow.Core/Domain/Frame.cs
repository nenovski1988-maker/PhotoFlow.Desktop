namespace PhotoFlow.Core.Domain;

public sealed class Frame
{
    public required string RawPath { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // По-късно ще добавим ProcessedPath, ExportedPaths, Status, Errors и т.н.
}
