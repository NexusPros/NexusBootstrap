using System.Net.Http.Json;

namespace NexusBootstrap.Services;

public record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc);

public record SyncResult(bool Success, string? LatestVersion, string? PackageUrl, string? PackageSha256, string? Error);

// CI/CD Phase 4 (2026-09-01) - Fresh-install mode's only Console dependency. Deliberately minimal:
// plain HttpClient + System.Net.Http.Json, no reference to the full NexusCloudClient project (that
// would pull in EF Core/Npgsql/other heavy dependencies Bootstrap has no business carrying - "keep
// Bootstrap tiny" is a hard boundary, not a starting point to grow from).
//
// HddSerial/ContractSerial are accepted here ONLY because they're the existing legacy migration
// identity iYamPOS already trusts and hands off directly (a local process launch, not a network
// transmission) - this is not a new security credential, and the resulting Bearer token from the
// token call is what authenticates the one Sync call that follows. Nothing here persists that
// token or reuses it for anything else - Bootstrap's job ends once it has PackageUrl/PackageSha256.
public class ConsoleBootstrapClient
{
    private readonly HttpClient _http;

    public ConsoleBootstrapClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SyncResult> ResolveLatestPackageAsync(string hddSerial, string contractSerial, CancellationToken ct = default)
    {
        try
        {
            var tokenResponse = await _http.PostAsJsonAsync("api/v2/installations/token", new
            {
                DeviceId = hddSerial,
                ContractSerial = contractSerial,
            }, ct);

            if (!tokenResponse.IsSuccessStatusCode)
                return new SyncResult(false, null, null, null, $"Token request failed: HTTP {(int)tokenResponse.StatusCode}.");

            var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                return new SyncResult(false, null, null, null, "Token response was empty.");

            using var syncRequest = new HttpRequestMessage(HttpMethod.Post, "api/v2/installations/sync");
            syncRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            syncRequest.Content = JsonContent.Create(new
            {
                // Deliberately a low placeholder, not a real installed version - Bootstrap in
                // fresh-install mode has no NexusBeta installed yet to report a real AppVersion
                // for, and the server only uses this to decide whether an update is "available" -
                // an always-old value ensures LatestVersion/PackageUrl always populate.
                AppVersion = "0.0.0",
                Latitude = (double?)null,
                Longitude = (double?)null,
                UnitTitle = "NexusBootstrap",
                ClientPlatform = "NexusBeta",
            });

            var syncResponse = await _http.SendAsync(syncRequest, ct);
            if (!syncResponse.IsSuccessStatusCode)
                return new SyncResult(false, null, null, null, $"Sync request failed: HTTP {(int)syncResponse.StatusCode}.");

            var sync = await syncResponse.Content.ReadFromJsonAsync<SyncPosResponseSubset>(cancellationToken: ct);
            if (sync == null || string.IsNullOrEmpty(sync.PackageUrl) || string.IsNullOrEmpty(sync.PackageSha256))
                return new SyncResult(false, null, null, null, "No active release is available for this installation right now.");

            return new SyncResult(true, sync.LatestVersion, sync.PackageUrl, sync.PackageSha256, null);
        }
        catch (Exception ex)
        {
            return new SyncResult(false, null, null, null, $"Console request failed: {ex.Message}");
        }
    }

    // Stable compatibility subset (2026-09-01, per the CI/CD plan's own locked principle) - only
    // the fields Bootstrap actually consumes from the real SyncPosResponse, not the full DTO. A
    // future change to the full response must keep these field names/shapes intact or bump a
    // version marker Bootstrap can detect - never a silent rename because "nothing else uses them."
    private class SyncPosResponseSubset
    {
        public string? LatestVersion { get; set; }
        public string? PackageUrl { get; set; }
        public string? PackageSha256 { get; set; }
    }
}