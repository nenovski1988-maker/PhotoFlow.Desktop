namespace PhotoFlow.Core.Domain;

public sealed class ProductSession
{
    public required string Barcode { get; init; }
    public string? ProductName { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; private set; }

    public bool IsFinalized => FinalizedAt != null;

    public List<Frame> Frames { get; } = new();

    public void FinalizeSession()
    {
        if (IsFinalized) return;
        FinalizedAt = DateTime.UtcNow;
    }
}
