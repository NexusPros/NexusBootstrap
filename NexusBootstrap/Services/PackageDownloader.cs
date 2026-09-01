using System.Security.Cryptography;

namespace NexusBootstrap.Services;

public record DownloadResult(bool Success, string? TempZipPath, string? Error);

// CI/CD Phase 4 (2026-09-01) - downloads to a temp file and verifies its SHA-256 against the
// AUTHENTICATED hash (passed in by the caller - either NexusBeta's own authenticated Sync response
// in Update mode, or ConsoleBootstrapClient's own authenticated Sync call in Fresh-install mode -
// never something parsed off the URL/filename). Hash verification happens here, before this result
// is ever handed to InstallationReplacer - a corrupted or tampered download must never reach the
// point of touching a real installation directory. Per the plan's own locked principle: the SAS
// URL is a temporary transport credential only: it does not establish trust by itself. This
// comparison against the authenticated hash is what actually verifies WHAT was downloaded.
public class PackageDownloader
{
    private readonly HttpClient _http;

    public PackageDownloader(HttpClient http)
    {
        _http = http;
    }

    public async Task<DownloadResult> DownloadAndVerifyAsync(string url, string expectedSha256, CancellationToken ct = default)
    {
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"nexusbeta-update-{Guid.NewGuid():N}.zip");

        try
        {
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    return new DownloadResult(false, null, $"Download failed: HTTP {(int)response.StatusCode}.");

                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await responseStream.CopyToAsync(fileStream, ct);
            }

            string actualSha256;
            await using (var verifyStream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hashBytes = await SHA256.HashDataAsync(verifyStream, ct);
                actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteTempFile(tempZipPath);
                return new DownloadResult(false, null, $"Hash mismatch - expected {expectedSha256}, got {actualSha256}. Refusing to install a package that doesn't match its authenticated hash.");
            }

            return new DownloadResult(true, tempZipPath, null);
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tempZipPath);
            return new DownloadResult(false, null, $"Download failed: {ex.Message}");
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only - a leftover temp file in %TEMP% is not worth failing over.
        }
    }
}