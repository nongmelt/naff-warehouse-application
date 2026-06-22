using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

namespace app.Services;

public enum EnrollResult { Success, Rejected, Unreachable }

public sealed record EnrollResponse(string MinioEndpoint, string Bucket, string AccessKey, string SecretKey);

[SupportedOSPlatform("windows")]
public static class EnrollClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// POST {ApiUrl}/enroll { stationName, stationKey, ipAddress }. On 200 saves the
    /// returned MinIO creds + resets the shared MinIO client. 400/401/403/404 -> Rejected;
    /// network/timeout -> Unreachable.
    public static async Task<EnrollResult> EnrollAsync(CancellationToken ct = default)
    {
        var stationName = AppSettings.StationName;
        var stationKey  = AppSettings.StationKey;
        if (string.IsNullOrWhiteSpace(stationName) || string.IsNullOrWhiteSpace(stationKey))
        {
            Logger.Log("EnrollClient: stationName/stationKey not set (appsettings.json) — cannot enroll");
            return EnrollResult.Rejected;
        }
        var body = new { stationName, stationKey, ipAddress = LocalIPv4() };
        try
        {
            var http = ApiService.GetHttpClient(); // BaseAddress = ApiUrl + "/"
            var res = await http.PostAsJsonAsync("enroll", body, ct);
            if (res.StatusCode == HttpStatusCode.OK)
            {
                var creds = await res.Content.ReadFromJsonAsync<EnrollResponse>(JsonOpts, ct);
                if (creds is null) return EnrollResult.Unreachable;
                AppSettings.SaveMinioCredentials(creds.MinioEndpoint, creds.Bucket, creds.AccessKey, creds.SecretKey);
                VideoWorkflowRunner.ResetMinioClient();
                Logger.Log($"EnrollClient: enrolled {stationName}");
                return EnrollResult.Success;
            }
            Logger.Log($"EnrollClient: enroll rejected, HTTP {(int)res.StatusCode}");
            return EnrollResult.Rejected; // 400 bad fields, 401 bad key, 404 unknown station, 403 CF
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Logger.Log($"EnrollClient: server unreachable — {ex.Message}");
            return EnrollResult.Unreachable;
        }
    }

    /// First non-loopback IPv4 on an operational interface; "" if none (self-reported, Docker-SNAT-safe).
    private static string LocalIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
            }
        }
        catch { }
        return "";
    }
}
