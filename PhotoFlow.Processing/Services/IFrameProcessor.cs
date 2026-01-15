using PhotoFlow.Core.Domain;

namespace PhotoFlow.Processing.Services;

public interface IFrameProcessor
{
    /// <summary>
    /// Processes raw frame into /processed and creates exports in /exports.
    /// Returns full paths of the created export files.
    /// </summary>
    Task<IReadOnlyList<string>> ProcessAsync(ProductSession session, Frame frame, ProcessingOptions options, CancellationToken ct = default);
}
