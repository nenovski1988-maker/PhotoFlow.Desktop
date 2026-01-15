namespace PhotoFlow.Core.Services;

public sealed class WorkspaceLayout : IWorkspaceLayout
{
    public WorkspaceLayout(string workspaceRoot)
    {
        WorkspaceRoot = workspaceRoot;
    }

    public string WorkspaceRoot { get; }

    public string GetProductRoot(string barcode)
        => Path.Combine(WorkspaceRoot, barcode);

    public string GetRawFolder(string barcode)
        => Path.Combine(GetProductRoot(barcode), "raw");

    public string GetProcessedFolder(string barcode)
        => Path.Combine(GetProductRoot(barcode), "processed");

    public string GetExportsFolder(string barcode)
        => Path.Combine(GetProductRoot(barcode), "exports");
}
