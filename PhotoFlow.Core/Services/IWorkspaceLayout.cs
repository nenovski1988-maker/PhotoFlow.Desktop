namespace PhotoFlow.Core.Services;

public interface IWorkspaceLayout
{
    string WorkspaceRoot { get; }
    string GetProductRoot(string barcode);
    string GetRawFolder(string barcode);
    string GetProcessedFolder(string barcode);
    string GetExportsFolder(string barcode);
}
