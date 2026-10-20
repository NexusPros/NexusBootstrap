using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NexusPostgresRestore.Models;
using NexusPostgresRestore.Recovery;

namespace NexusBootstrap.Restore;

// Bootstrap's Console calls for a restore (restore plan v2, API contract V2.9 rows 1, 11, 12):
//   POST api/v2/installations/token                          DeviceId + ContractSerial (activation.dat) -> device token
//   GET  api/v2/backups/restore-requests/{id}/grant?jobId=   token + client key -> the grant
//   POST api/v2/backups/restore-requests/{id}/report         token + client key -> Started / Failed / RolledBack / SupportNeeded
// The token lives in memory only (re-minted once on 401). The client key (P1) is NexusBeta's per-request secret, handed
// over on standard input: sent ONLY as the X-Nexus-Restore-Key header (64 hex characters; the Console keeps
// hex(SHA-256(the 32 key bytes)) - S1b). Until S1b the Console ignores the header. Never logged.
internal sealed class ConsoleRestoreClient(HttpClient http, Activation activation, string? clientKey) : IRestoreConsole
{
    public const string KeyHeader = "X-Nexus-Restore-Key";
    private string? _token;

    public async Task<RestoreGrant?> GetGrantAsync(Guid requestId, Guid jobId, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                $"api/v2/backups/restore-requests/{requestId}/grant?jobId={jobId}"), ct);
            if (response == null || (int)response.StatusCode >= 500) return null;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<RestoreGrant>(ct);
            return new RestoreGrant { RequestId = requestId, Usable = false, Reason = await MessageAsync(response, ct) };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<(ReportAck Ack, string? Message)> ReportAsync(RestoreJournal journal, OutboxReport report, CancellationToken ct)
    {
        var body = new
        {
            journal.JobId,
            report.Status,
            report.Reason,
            // Started: the BeforeRestore copy's identity - an Undo is bound to exactly these two files (S1b).
            SafetyBackup = report.Status == "Started"
                ? new { journal.Safety.TakenAtUtc, OrganizationDbSha256 = journal.Safety.Organization.Sha256, BranchDbSha256 = journal.Safety.Branch.Sha256 }
                : null,
            report.Case,
            ReportedAtUtc = report.AtUtc,
        };
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                $"api/v2/backups/restore-requests/{journal.RequestId}/report") { Content = JsonContent.Create(body) }, ct);
            if (response == null || (int)response.StatusCode >= 500) return (ReportAck.Unreachable, null);
            return response.IsSuccessStatusCode
                ? (ReportAck.Acknowledged, null)
                : (ReportAck.Refused, await MessageAsync(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (ReportAck.Unreachable, null);
        }
    }

    // null = no token could be obtained (Console unreachable, or this device is not accepted).
    private async Task<HttpResponseMessage?> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_token == null && !await MintTokenAsync(ct)) return null;
            var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (clientKey != null) request.Headers.Add(KeyHeader, clientKey);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt == 1) return response;
            response.Dispose();
            _token = null;   // expired / rejected: mint once more
        }
        return null;
    }

    private async Task<bool> MintTokenAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/v2/installations/token",
            new { activation.DeviceId, activation.ContractSerial }, ct);
        if (!response.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = doc.RootElement.TryGetProperty("accessToken", out var t) || doc.RootElement.TryGetProperty("AccessToken", out t) ? t.GetString() : null;
        return !string.IsNullOrEmpty(_token);
    }

    // The Console's error text ({ message } / { Message }), else the status code.
    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var name in new[] { "message", "Message", "error", "Error" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString()!;
        }
        catch (JsonException) { /* not JSON */ }
        return $"HTTP {(int)response.StatusCode}";
    }
}
