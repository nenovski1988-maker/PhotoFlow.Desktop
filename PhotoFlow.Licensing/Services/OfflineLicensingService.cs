using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace PhotoFlow.Licensing.Services;

// Offline licensing (file + RSA signature) + local TRIAL fallback.
public sealed class OfflineLicensingService : ILicensingService
{
    private readonly LicenseCheckResult _res;

    public OfflineLicensingService()
    {
        _res = LicenseVerifier.LoadAndVerify();
    }

    public bool IsValid() => _res.IsValid;

    public string GetStatusText() => _res.StatusText;

    public Entitlements GetEntitlements()
    {
        if (!_res.IsValid || _res.Payload is null)
            return new Entitlements(
                AiBackgroundRemovalAllowed: false,
                MaxProductsPerDay: 0,
                MaxFramesPerProduct: 0,
                WatermarkRequired: true
            );

        var p = _res.Payload;

        var type = (string.IsNullOrWhiteSpace(p.Type) ? "LICENSE" : p.Type).Trim().ToUpperInvariant();
        if (type == "PAID") type = "LICENSE";

        if (type == "TRIAL")
        {
            return new Entitlements(
                AiBackgroundRemovalAllowed: true,
                MaxProductsPerDay: 10,
                MaxFramesPerProduct: 50,
                WatermarkRequired: true
            );
        }

        if (type == "MONTHLY" || type == "LICENSE")
        {
            return new Entitlements(
                AiBackgroundRemovalAllowed: true,
                MaxProductsPerDay: int.MaxValue,
                MaxFramesPerProduct: int.MaxValue,
                WatermarkRequired: false
            );
        }

        return new Entitlements(
            AiBackgroundRemovalAllowed: false,
            MaxProductsPerDay: 0,
            MaxFramesPerProduct: 0,
            WatermarkRequired: true
        );
    }

    public LicensePayload? DebugPayload => _res.Payload;
}

internal static class LicenseVerifier
{
    // Documents\PhotoFlow\Licenses\photoflow.license.json
    private static string LicensePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoFlow", "Licenses", "photoflow.license.json"
        );

    // IMPORTANT: trial duration here
    private const int TrialDays = 14;

    // Public key DER (base64, one line)
    private const string PublicKeyB64 =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAqpp05LNm4YHA2uRvjqreviFCXpo8fAx25k9deTZYDSEY6id/eszqCuSj0FbMAExLmC2Vhsiwct0vyUMKtY0AoWGiw6ncKEoPVXIL7NmMkhdJJgtbpuwlRRqb437C01stH7pedT38cRZJGfbzPV3G5ln7OxB42EVk1sC58Ejm9bneENfDIQ23XA6tn+fTS5GWAyrT7DXzg+6sZlZkTw0IzpcvTlM7cQmXCEdRCCyDPaDVyrRe5nRFZ0dGZ+gVDcwbduB+O7rqyImwn2dIMGJo6t9onfYuJS88o/PW2jN2Mhdv+gif2+GvFLY2H9gfhzeQCITM8eYMmLIlxMUm+I70ee2f9B/XcnKY2+PGWnf929tE30XMgYs30INDfxA2o1no463Hk97rN6B7yZcEWFld7wx9mMMbhSC/c87mCQcq1pTsOJ8ELWwtY6z5/KXzo2nSFt2ApJ819LZoAj8Cqp8F1pEuTduGsjwU/W/W+8DPJsvf9sMlPdxW7m1KWxvcKJRpAgMBAAE=";

    private static string NormalizeType(string? t)
    {
        var type = string.IsNullOrWhiteSpace(t) ? "LICENSE" : t.Trim().ToUpperInvariant();
        if (type == "PAID") type = "LICENSE";
        return type;
    }

    public static LicenseCheckResult LoadAndVerify()
    {
        try
        {
            // 1) If NO paid license file -> auto TRIAL
            if (!File.Exists(LicensePath))
            {
                var trial = TrialStore.GetOrCreateTrial(TrialDays);

                if (!trial.IsOk)
                    return LicenseCheckResult.Fail(trial.ErrorMessage ?? "Trial: ERROR");

                // create a pseudo payload so the rest of the app works unchanged
                var trialPayload = new LicensePayload
                {
                    Product = "PhotoFlow",
                    Type = "TRIAL",
                    Customer = string.IsNullOrWhiteSpace(trial.Customer) ? "TRIAL" : trial.Customer!,
                    Seats = 1,
                    LicenseId = trial.TrialId ?? "TRIAL",
                    IssuedUtc = trial.IssuedUtc,
                    ExpiresUtc = trial.ExpiresUtc
                };

                var daysLeft = (int)Math.Ceiling((trialPayload.ExpiresUtc!.Value - DateTime.UtcNow).TotalDays);
                if (daysLeft < 0) daysLeft = 0;

                return LicenseCheckResult.Ok(
                    $"{trialPayload.Customer}  |  TRIAL  |  expires: {trialPayload.ExpiresUtc.Value:yyyy-MM-dd}  |  days left: {daysLeft}",
                    trialPayload
                );
            }

            // 2) Paid license path: verify RSA signature
            var json = File.ReadAllText(LicensePath, Encoding.UTF8);

            var lic = JsonSerializer.Deserialize<LicenseFile>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (lic == null || string.IsNullOrWhiteSpace(lic.payloadB64) || string.IsNullOrWhiteSpace(lic.sigB64))
                return LicenseCheckResult.Fail("License: Invalid (bad file format).");

            var payloadBytes = Convert.FromBase64String(lic.payloadB64.Trim());
            var sigBytes = Convert.FromBase64String(lic.sigB64.Trim());

            if (!VerifySignature(payloadBytes, sigBytes))
                return LicenseCheckResult.Fail("License: Invalid signature.");

            var payloadText = Encoding.UTF8.GetString(payloadBytes);
            var payload = LicensePayload.Parse(payloadText);

            if (!string.Equals(payload.Product, "PhotoFlow", StringComparison.OrdinalIgnoreCase))
                return LicenseCheckResult.Fail("License: Wrong product.");

            var type = NormalizeType(payload.Type);

            var now = DateTime.UtcNow;

            if ((type == "TRIAL" || type == "MONTHLY") && !payload.ExpiresUtc.HasValue)
                return LicenseCheckResult.Fail($"License: {type} requires ExpiresUtc.", payload);

            if (payload.ExpiresUtc.HasValue && now > payload.ExpiresUtc.Value)
                return LicenseCheckResult.Fail($"License: Expired ({payload.ExpiresUtc.Value:yyyy-MM-dd}).", payload);

            var exp = payload.ExpiresUtc.HasValue ? payload.ExpiresUtc.Value.ToString("yyyy-MM-dd") : "never";
            var who = string.IsNullOrWhiteSpace(payload.Customer) ? "Unknown customer" : payload.Customer.Trim();
            var seats = payload.Seats > 0 ? payload.Seats.ToString() : "?";

            return LicenseCheckResult.Ok($"{who}  |  {type}  |  seats: {seats}  |  expires: {exp}", payload);
        }
        catch (FormatException)
        {
            return LicenseCheckResult.Fail("License: Corrupt (base64 decode failed).");
        }
        catch (Exception ex)
        {
            return LicenseCheckResult.Fail("License: ERROR (" + ex.Message + ")");
        }
    }

    private static bool VerifySignature(byte[] payloadBytes, byte[] sigBytes)
    {
        var pubDer = Convert.FromBase64String(PublicKeyB64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(pubDer, out _);

        return rsa.VerifyData(
            payloadBytes,
            sigBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
    }

    private sealed class LicenseFile
    {
        public string? payloadB64 { get; set; }
        public string? sigB64 { get; set; }
    }
}

internal sealed class LicenseCheckResult
{
    public bool IsValid { get; }
    public string StatusText { get; }
    public LicensePayload? Payload { get; }

    private LicenseCheckResult(bool ok, string text
        , LicensePayload? payload)
    {
        IsValid = ok;
        StatusText = text;
        Payload = payload;
    }

    public static LicenseCheckResult Ok(string text, LicensePayload payload) =>
        new(true, "License: " + text, payload);

    public static LicenseCheckResult Fail(string text, LicensePayload? payload = null) =>
        new(false, text, payload);
}

public sealed class LicensePayload
{
    public string Product { get; set; } = "";
    public string LicenseId { get; set; } = "";
    public string Customer { get; set; } = "";
    public int Seats { get; set; }
    public string Type { get; set; } = "";
    public DateTime? IssuedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    public static LicensePayload Parse(string text)
    {
        var p = new LicensePayload();

        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();

            switch (key.ToUpperInvariant())
            {
                case "PRODUCT":
                    p.Product = val;
                    break;
                case "LICENSEID":
                    p.LicenseId = val;
                    break;
                case "CUSTOMER":
                    p.Customer = val;
                    break;
                case "SEATS":
                    if (int.TryParse(val, out var s)) p.Seats = s;
                    break;
                case "TYPE":
                    p.Type = val;
                    break;
                case "ISSUEDUTC":
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iu))
                        p.IssuedUtc = iu;
                    break;
                case "EXPIRESUTC":
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var eu))
                        p.ExpiresUtc = eu;
                    break;
            }
        }

        return p;
    }
}

/// <summary>
/// Local TRIAL store (no server). DPAPI + HMAC + basic clock rollback detection.
/// Stored under %LOCALAPPDATA%\PhotoFlow\trial.dat
/// </summary>
internal static class TrialStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string TrialPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoFlow", "trial.dat");

    // "entropy" makes DPAPI output different for other apps
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("PhotoFlow.Trial.v1");

    // HMAC secret (obfuscation only; still helps catch casual tampering)
    private static readonly byte[] HmacKey = Encoding.UTF8.GetBytes("PhotoFlow::Trial::HMAC::v1::change-this-string");

    internal sealed class TrialResult
    {
        public bool IsOk { get; init; }
        public string? ErrorMessage { get; init; }
        public string? TrialId { get; init; }
        public string? Customer { get; init; }
        public DateTime IssuedUtc { get; init; }
        public DateTime ExpiresUtc { get; init; }
    }

    private sealed class TrialEnvelope
    {
        public string? dataB64 { get; set; }
        public string? sigB64 { get; set; }
    }

    private sealed class TrialCore
    {
        public string machine { get; set; } = "";
        public DateTime firstRunUtc { get; set; }
        public DateTime lastRunUtc { get; set; }
        public DateTime expiresUtc { get; set; }
    }

    public static TrialResult GetOrCreateTrial(int days)
    {
        try
        {
            var now = DateTime.UtcNow;
            var machine = GetMachineFingerprint();

            // Try load
            var loaded = TryLoad(machine);
            if (loaded != null)
            {
                // clock rollback check (allow small drift)
                if (now < loaded.lastRunUtc.AddHours(-6))
                    return new TrialResult { IsOk = false, ErrorMessage = "Trial: System clock rollback detected." };

                if (now > loaded.expiresUtc)
                    return new TrialResult { IsOk = false, ErrorMessage = $"Trial: Expired ({loaded.expiresUtc:yyyy-MM-dd})." };

                // update lastRun (only forward)
                if (now > loaded.lastRunUtc)
                {
                    loaded.lastRunUtc = now;
                    Save(machine, loaded);
                }

                return new TrialResult
                {
                    IsOk = true,
                    TrialId = "TRIAL-" + ShortId(machine),
                    Customer = Environment.UserName,
                    IssuedUtc = loaded.firstRunUtc,
                    ExpiresUtc = loaded.expiresUtc
                };
            }

            // Create new
            var core = new TrialCore
            {
                machine = machine,
                firstRunUtc = now,
                lastRunUtc = now,
                expiresUtc = now.AddDays(days)
            };

            Save(machine, core);

            return new TrialResult
            {
                IsOk = true,
                TrialId = "TRIAL-" + ShortId(machine),
                Customer = Environment.UserName,
                IssuedUtc = core.firstRunUtc,
                ExpiresUtc = core.expiresUtc
            };
        }
        catch (Exception ex)
        {
            return new TrialResult { IsOk = false, ErrorMessage = "Trial: ERROR (" + ex.Message + ")" };
        }
    }

    private static TrialCore? TryLoad(string machine)
    {
        if (!File.Exists(TrialPath))
            return null;

        var protectedBytes = File.ReadAllBytes(TrialPath);

        byte[] envBytes;
        try
        {
            envBytes = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        }
        catch
        {
            // can't unprotect => treat as invalid
            return null;
        }

        var envJson = Encoding.UTF8.GetString(envBytes);
        var env = JsonSerializer.Deserialize<TrialEnvelope>(envJson, JsonOpts);
        if (env == null || string.IsNullOrWhiteSpace(env.dataB64) || string.IsNullOrWhiteSpace(env.sigB64))
            return null;

        var dataBytes = Convert.FromBase64String(env.dataB64.Trim());
        var sigBytes = Convert.FromBase64String(env.sigB64.Trim());

        if (!VerifyHmac(dataBytes, sigBytes))
            return null;

        var coreJson = Encoding.UTF8.GetString(dataBytes);
        var core = JsonSerializer.Deserialize<TrialCore>(coreJson, JsonOpts);
        if (core == null)
            return null;

        if (!string.Equals(core.machine, machine, StringComparison.Ordinal))
            return null;

        return core;
    }

    private static void Save(string machine, TrialCore core)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TrialPath)!);

        // serialize core
        var coreJson = JsonSerializer.Serialize(core);
        var dataBytes = Encoding.UTF8.GetBytes(coreJson);
        var sigBytes = ComputeHmac(dataBytes);

        var env = new TrialEnvelope
        {
            dataB64 = Convert.ToBase64String(dataBytes),
            sigB64 = Convert.ToBase64String(sigBytes)
        };

        var envJson = JsonSerializer.Serialize(env);
        var envBytes = Encoding.UTF8.GetBytes(envJson);

        var protectedBytes = ProtectedData.Protect(envBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TrialPath, protectedBytes);
    }

    private static byte[] ComputeHmac(byte[] data)
    {
        using var h = new HMACSHA256(HmacKey);
        return h.ComputeHash(data);
    }

    private static bool VerifyHmac(byte[] data, byte[] sig)
    {
        var expected = ComputeHmac(data);
        return CryptographicOperations.FixedTimeEquals(expected, sig);
    }

    private static string GetMachineFingerprint()
    {
        // stable enough for your scenario (per machine + per user SID)
        var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? "NO_SID";
        var raw = $"{Environment.MachineName}|{sid}|PhotoFlow";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash); // 64 chars
    }

    private static string ShortId(string machineFingerprint)
    {
        // first 10 chars are enough for display
        return machineFingerprint.Length >= 10 ? machineFingerprint.Substring(0, 10) : machineFingerprint;
    }
}
