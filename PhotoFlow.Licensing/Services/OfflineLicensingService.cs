using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoFlow.Licensing.Services;

// Offline licensing (file + RSA signature).
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
        // Тук по принцип няма да стига, ако си блокирал старта при невалиден лиценз,
        // но оставяме safe default.
        if (!_res.IsValid || _res.Payload is null)
            return new Entitlements(
                AiBackgroundRemovalAllowed: false,
                MaxProductsPerDay: 0,
                MaxFramesPerProduct: 0,
                WatermarkRequired: true
            );

        var p = _res.Payload;

        // TRIAL => watermark ON
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

        // MONTHLY и LICENSE са “платени” (watermark OFF)
        if (type == "MONTHLY" || type == "LICENSE")
        {
            return new Entitlements(
                AiBackgroundRemovalAllowed: true,
                MaxProductsPerDay: int.MaxValue,
                MaxFramesPerProduct: int.MaxValue,
                WatermarkRequired: false
            );
        }

        // Ако се появи непознат тип — по-безопасно е да НЕ го приемаме като платен.
        return new Entitlements(
            AiBackgroundRemovalAllowed: false,
            MaxProductsPerDay: 0,
            MaxFramesPerProduct: 0,
            WatermarkRequired: true
        );
    }

    // По желание за debug (не е в интерфейса)
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

    // Тук е photoflow_lic_public.b64 (една линия, без нови редове)
    private const string PublicKeyB64 =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAqpp05LNm4YHA2uRvjqreviFCXpo8fAx25k9deTZYDSEY6id/eszqCuSj0FbMAExLmC2Vhsiwct0vyUMKtY0AoWGiw6ncKEoPVXIL7NmMkhdJJgtbpuwlRRqb437C01stH7pedT38cRZJGfbzPV3G5ln7OxB42EVk1sC58Ejm9bneENfDIQ23XA6tn+fTS5GWAyrT7DXzg+6sZlZkTw0IzpcvTlM7cQmXCEdRCCyDPaDVyrRe5nRFZ0dGZ+gVDcwbduB+O7rqyImwn2dIMGJo6t9onfYuJS88o/PW2jN2Mhdv+gif2+GvFLY2H9gfhzeQCITM8eYMmLIlxMUm+I70ee2f9B/XcnKY2+PGWnf929tE30XMgYs30INDfxA2o1no463Hk97rN6B7yZcEWFld7wx9mMMbhSC/c87mCQcq1pTsOJ8ELWwtY6z5/KXzo2nSFt2ApJ819LZoAj8Cqp8F1pEuTduGsjwU/W/W+8DPJsvf9sMlPdxW7m1KWxvcKJRpAgMBAAE=";

    private static string NormalizeType(string? t)
    {
        var type = string.IsNullOrWhiteSpace(t) ? "LICENSE" : t.Trim().ToUpperInvariant();

        // За обратна съвместимост: PAID = LICENSE
        if (type == "PAID") type = "LICENSE";

        return type;
    }

    public static LicenseCheckResult LoadAndVerify()
    {
        try
        {
            if (!File.Exists(LicensePath))
                return LicenseCheckResult.Fail("License: Missing (no license file).");

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

            var type = NormalizeType(payload.Type); // ✅ дефинираме type ПРЕДИ да го ползваме

            var now = DateTime.UtcNow;

            // TRIAL и MONTHLY: ExpiresUtc е задължително
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

    private LicenseCheckResult(bool ok, string text, LicensePayload? payload)
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

// Public, за да е видим между проекти
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

        var lines = text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

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
