using System.Text;
using System.Text.Json;

namespace PhotoFlow.Licensing.Trial;

public static class TrialLicenseWriter
{
    public static string GetLicensePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PhotoFlow", "Licenses", "photoflow.license.json"
        );
    }

    public static void SaveEnvelopeAsLicenseFile(TrialClient.LicenseEnvelope env)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetLicensePath())!);

        // payload JSON -> bytes -> base64
        var payloadJson = JsonSerializer.Serialize(env.Payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var payloadB64 = Convert.ToBase64String(payloadBytes);

        // signature already base64 (server returned base64)
        var lic = new
        {
            payloadB64 = payloadB64,
            sigB64 = env.SignatureBase64
        };

        var licJson = JsonSerializer.Serialize(lic, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(GetLicensePath(), licJson, Encoding.UTF8);
    }
}
