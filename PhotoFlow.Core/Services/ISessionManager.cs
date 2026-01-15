using PhotoFlow.Core.Domain;

namespace PhotoFlow.Core.Services;

public interface ISessionManager
{
    ProductSession? ActiveSession { get; }
    IReadOnlyList<ProductSession> RecentSessions { get; }

    ProductSession BeginOrSwitch(string barcode, string? productName = null);
    void FinalizeActive();

    /// <summary>
    /// Adds a new incoming image file to the active session by copying it into /raw
    /// </summary>
    Frame AddIncomingFile(string incomingFilePath);
}
