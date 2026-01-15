using PhotoFlow.Core.Domain;

namespace PhotoFlow.Core.Services;

public sealed class SessionManager : ISessionManager
{
    private readonly IWorkspaceLayout _workspace;
    private readonly List<ProductSession> _recent = new();
    private ProductSession? _active;

    public SessionManager(IWorkspaceLayout workspace)
    {
        _workspace = workspace;
        Directory.CreateDirectory(_workspace.WorkspaceRoot);
    }

    public ProductSession? ActiveSession => _active;

    public IReadOnlyList<ProductSession> RecentSessions => _recent;

    public ProductSession BeginOrSwitch(string barcode, string? productName = null)
    {
        barcode = NormalizeBarcode(barcode);

        if (_active is null)
        {
            _active = CreateNew(barcode, productName);
            AddToRecent(_active);
            EnsureFolders(_active.Barcode);
            return _active;
        }

        // If same barcode, just update product name (no finalize)
        if (string.Equals(_active.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(productName))
                _active.ProductName = productName;
            return _active;
        }

        // Switch to a new barcode: finalize previous, create new
        _active.FinalizeSession();

        _active = CreateNew(barcode, productName);
        AddToRecent(_active);
        EnsureFolders(_active.Barcode);
        return _active;
    }

    public void FinalizeActive()
    {
        _active?.FinalizeSession();
        _active = null;
    }

    public Frame AddIncomingFile(string incomingFilePath)
    {
        if (_active is null)
            throw new InvalidOperationException("No active product session. Scan/enter a barcode first.");

        if (string.IsNullOrWhiteSpace(incomingFilePath))
            throw new ArgumentException("Incoming file path is empty.");

        if (!File.Exists(incomingFilePath))
            throw new FileNotFoundException("Incoming file does not exist.", incomingFilePath);

        EnsureFolders(_active.Barcode);

        // Copy into /raw with unique name
        var rawFolder = _workspace.GetRawFolder(_active.Barcode);
        var ext = Path.GetExtension(incomingFilePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

        var destName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}";
        var destPath = Path.Combine(rawFolder, destName);

        FileHelpers.WaitForFileReady(incomingFilePath, timeoutMs: 5000);

        File.Copy(incomingFilePath, destPath, overwrite: false);

        // Try delete original (optional). If tether software locks it, ignore.
        try { File.Delete(incomingFilePath); } catch { /* ignore */ }

        var frame = new Frame { RawPath = destPath };
        _active.Frames.Add(frame);

        return frame;
    }

    private static string NormalizeBarcode(string barcode)
        => barcode.Trim();

    private ProductSession CreateNew(string barcode, string? productName)
        => new ProductSession
        {
            Barcode = barcode,
            ProductName = string.IsNullOrWhiteSpace(productName) ? null : productName.Trim(),
        };

    private void AddToRecent(ProductSession session)
    {
        // Keep most recent first, unique by barcode+createdAt not necessary now
        _recent.Insert(0, session);

        // Limit to last 10 sessions (you can change this later)
        while (_recent.Count > 10)
            _recent.RemoveAt(_recent.Count - 1);
    }

    private void EnsureFolders(string barcode)
    {
        Directory.CreateDirectory(_workspace.GetProductRoot(barcode));
        Directory.CreateDirectory(_workspace.GetRawFolder(barcode));
        Directory.CreateDirectory(_workspace.GetProcessedFolder(barcode));
        Directory.CreateDirectory(_workspace.GetExportsFolder(barcode));
    }

}
