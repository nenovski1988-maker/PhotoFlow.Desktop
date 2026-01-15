namespace PhotoFlow.Desktop.Models;

public sealed class AppSettings
{
    public List<string> SelectedExportNames { get; set; } = new();

    // NEW: folder settings (optional; if missing -> use defaults)
    public string? WorkspaceRoot { get; set; }
    public string? IncomingFolder { get; set; }
}
