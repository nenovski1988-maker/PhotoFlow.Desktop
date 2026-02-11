using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoFlow.Licensing.Trial;

public sealed class OfflineTrialState
{
    public DateTimeOffset FirstRunUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset LastRunUtc { get; set; }
}

public static class OfflineTrialStore
{
    private static string TrialFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoFlow",
            "trial.dat"
        );

    public static OfflineTrialState LoadOrCreate(int trialDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TrialFilePath)!);
        var now = DateTimeOffset.UtcNow;

        if (!File.Exists(TrialFilePath))
        {
            var st = new OfflineTrialState
            {
                FirstRunUtc = now,
                ExpiresUtc = now.AddDays(trialDays),
                LastRunUtc = now
            };

            Save(st);
            return st;
        }

        var existing = Load();

        // basic clock rollback detection
        if (now < existing.LastRunUtc - TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("System time rollback detected.");

        existing.LastRunUtc = now;
        Save(existing);
        return existing;
    }

    public static void ResetForDevOnly()
    {
        if (File.Exists(TrialFilePath))
            File.Delete(TrialFilePath);
    }

    private static OfflineTrialState Load()
    {
        var bytes = File.ReadAllBytes(TrialFilePath);
        var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<OfflineTrialState>(json)!;
    }

    private static void Save(OfflineTrialState st)
    {
        var json = JsonSerializer.Serialize(st);
        var plain = Encoding.UTF8.GetBytes(json);
        var bytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TrialFilePath, bytes);
    }
}
