using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFlow.Ingest.FolderIngest;

public sealed class IncomingFolderWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;

    // We dedupe by "signature" so overwrites/re-copies can still be detected.
    private readonly Dictionary<string, FileSig> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    public string IncomingFolder { get; }

    public event Action<string>? FileArrived;
    public event Action<string>? Info;
    public event Action<string>? Error;

    // Tuning knobs (safe defaults for camera transfer tools)
    private const int InitialDelayMs = 150;
    private const int StableChecksRequired = 3;
    private const int StableCheckIntervalMs = 200;
    private const int MaxWaitMs = 12_000;

    public IncomingFolderWatcher(string incomingFolder)
    {
        IncomingFolder = incomingFolder;
        Directory.CreateDirectory(IncomingFolder);
    }

    public void Start()
    {
        if (_watcher != null) return;

        _watcher = new FileSystemWatcher(IncomingFolder)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += (_, e) => EnqueueCandidate(e.FullPath);
        _watcher.Renamed += (_, e) => EnqueueCandidate(e.FullPath);
        _watcher.Changed += (_, e) => EnqueueCandidate(e.FullPath);

        Info?.Invoke($"Watching incoming folder: {IncomingFolder} (JPEG only)");
    }

    public void Stop()
    {
        if (_watcher == null) return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        finally
        {
            _watcher = null;
            Info?.Invoke("Stopped watching incoming folder.");
        }
    }

    private void EnqueueCandidate(string path)
    {
        // Fire-and-forget, but serialize processing so we don't stampede the pipeline.
        _ = Task.Run(() => HandleCandidateAsync(path, _cts.Token));
    }

    private async Task HandleCandidateAsync(string path, CancellationToken ct)
    {
        // serialize all candidate handling
        await _queueGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested) return;

            if (!IsEligibleJpeg(path))
                return;

            // some tools create then rename; give it a moment
            await Task.Delay(InitialDelayMs, ct).ConfigureAwait(false);

            // wait until copy/write finishes
            if (!await WaitForStableFileAsync(path, ct).ConfigureAwait(false))
                return;

            // after stable, create signature and dedupe
            FileSig sig;
            try
            {
                var fi = new FileInfo(path);
                sig = new FileSig(fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                // file disappeared between checks
                return;
            }

            bool isNew;
            lock (_gate)
            {
                if (_seen.TryGetValue(path, out var prev) && prev.Equals(sig))
                {
                    isNew = false;
                }
                else
                {
                    _seen[path] = sig;
                    isNew = true;
                }
            }

            if (!isNew)
                return;

            Info?.Invoke($"Incoming ready: {Path.GetFileName(path)}");
            FileArrived?.Invoke(path);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Error?.Invoke("Incoming watcher error: " + ex.Message);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private static bool IsEligibleJpeg(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // ignore temp/partial files commonly produced by transfer tools/browsers
        // (even if you "only use jpeg", these happen during transfer)
        if (fileName.StartsWith("~", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.StartsWith(".", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)) return false;

        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        return ext is ".jpg" or ".jpeg";
    }

    private async Task<bool> WaitForStableFileAsync(string path, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        long lastSize = -1;
        int stableCount = 0;

        while (sw.ElapsedMilliseconds < MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // quick existence check
                if (!File.Exists(path))
                {
                    await Task.Delay(StableCheckIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                var fi = new FileInfo(path);
                long sizeNow = fi.Length;

                // size stability check
                if (sizeNow > 0 && sizeNow == lastSize)
                    stableCount++;
                else
                    stableCount = 0;

                lastSize = sizeNow;

                // try open-read to ensure writer released the file (or at least it's readable)
                // NOTE: Some tools keep handle open with share-read; this is why we don't require exclusive access.
                if (stableCount >= StableChecksRequired)
                {
                    if (CanOpenForRead(path))
                        return true;
                }
            }
            catch
            {
                // ignore and retry (file might still be moving)
            }

            await Task.Delay(StableCheckIntervalMs, ct).ConfigureAwait(false);
        }

        Info?.Invoke($"Incoming skipped (not stable): {Path.GetFileName(path)}");
        return false;
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch { /* ignore */ }

        Stop();

        try
        {
            _cts.Dispose();
            _queueGate.Dispose();
        }
        catch { /* ignore */ }
    }

    private readonly record struct FileSig(long Length, long LastWriteTicks);
}
