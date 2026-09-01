using System.Diagnostics;

namespace NexusBootstrap.Services;

// CI/CD Phase 4 (2026-09-01) - launches the new version and does a simple "did it stay running
// past N seconds" health check. Deliberately simple for MVP (real health-checking - e.g. waiting
// for the app to report itself ready - is future work, not needed for this to be safe); this is
// enough to catch the actual failure mode that matters: a build that crashes immediately on
// startup, which is exactly what MinimumBootstrapVersion-style incompatibilities or a bad build
// would look like.
public class ProcessLauncher
{
    public async Task<bool> LaunchAndConfirmAsync(string exePath, TimeSpan confirmAfter, CancellationToken ct = default)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            UseShellExecute = true,
        });

        if (process == null)
            return false;

        await Task.Delay(confirmAfter, ct);

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Process already exited and was disposed/reaped between the delay and this check -
            // same outcome as HasExited == true.
            return false;
        }
    }
}