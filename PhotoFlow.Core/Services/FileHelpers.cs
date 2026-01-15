namespace PhotoFlow.Core.Services;

public static class FileHelpers
{
    public static void WaitForFileReady(string path, int timeoutMs)
    {
        var start = Environment.TickCount;

        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 0) return;
            }
            catch
            {
                // file is still being written/locked
            }

            if (Environment.TickCount - start > timeoutMs)
                throw new IOException($"File not ready within timeout: {path}");

            Thread.Sleep(150);
        }
    }
}
