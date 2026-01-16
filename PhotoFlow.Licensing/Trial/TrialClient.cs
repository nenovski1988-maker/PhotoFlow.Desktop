using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PhotoFlow.Licensing.Trial;

public sealed class TrialClient
{
    // Засега е localhost. После ще го сменим с истински домейн.
    private const string TrialServerBaseUrl = "https://localhost:7058";

    public async Task<LicenseEnvelope?> StartTrialAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();

        var req = new TrialStartRequest(
            AppId: "PhotoFlow",
            MachineId: GetMachineId()
        );

        using var resp = await http.PostAsJsonAsync($"{TrialServerBaseUrl}/api/trial/start", req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadFromJsonAsync<LicenseEnvelope>(cancellationToken: ct);
    }

    // ---- DTOs (трябва да съвпаднат с тези от сървъра) ----
    public sealed record TrialStartRequest(string AppId, string MachineId);

    public sealed record LicensePayload(
        string AppId,
        string Customer,
        string Type,
        int Seats,
        DateTimeOffset IssuedUtc,
        DateTimeOffset ExpiresUtc,
        string MachineId
    );

    public sealed record LicenseEnvelope(LicensePayload Payload, string SignatureBase64);

    // ---- MachineId ----
    private static string GetMachineId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(guid))
                return Sha256Hex(guid);
        }
        catch { /* ignore */ }

        var raw = $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.UserName}";
        return Sha256Hex(raw);
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
