using System.Diagnostics;
using NexusBootstrap;
using NexusBootstrap.Models;
using NexusBootstrap.Services;

// NexusBootstrap - CI/CD Phase 4 (2026-09-01). The permanent install/update executable for
// NexusBeta. Job stays exactly: download, verify, install, launch, recover - registration logic,
// entitlement logic, database access, and any other Console-domain behavior belong in NexusBeta/
// Console, never here. See the NexusCommunications/CI-CD plan (NexusProBeta repo) for the full
// locked design this implements.
//
// Two invocation modes:
//   Update mode (from an already-running NexusBeta, via NexusUpdateGate.razor):
//     NexusBootstrap.exe --url <PackageUrl> --sha256 <hash> --relaunch <NexusBeta.exe path>
//   Fresh-install mode (from iYamPOS's "Upgrade to NexusBeta" menu item):
//     NexusBootstrap.exe --fresh-install --hddSerial <value> --contractSerial <value>
// Console's base URL is no longer a caller-supplied argument (2026-09-01, Phase 5 recheck) - see
// BootstrapEndpoints.cs. Also: --version, standalone, prints the assembly version and exits.

var args_ = ParseArgs(args);

// Checked before any Require(...) validation - --version must work standalone even when no other
// arguments are present at all.
if (args_.ContainsKey("version"))
{
    Console.WriteLine(typeof(BootstrapEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown");
    return 0;
}

string targetDir;
string? packageUrl;
string? packageSha256;
string? latestVersion = null;

try
{
    if (args_.ContainsKey("fresh-install"))
    {
        targetDir = InstallationDirectoryResolver.ResolveForFreshInstall();
        Console.WriteLine($"Fresh-install mode. Target directory: {targetDir}");

        Require(args_, "hddSerial");
        var contractSerial = args_.GetValueOrDefault("contractSerial", "");

        using var httpClient = new HttpClient { BaseAddress = new Uri(BootstrapEndpoints.ConsoleBaseUrl) };
        var consoleClient = new ConsoleBootstrapClient(httpClient);
        var syncResult = await consoleClient.ResolveLatestPackageAsync(args_["hddSerial"], contractSerial);

        if (!syncResult.Success)
        {
            Console.Error.WriteLine($"Could not resolve a release to install: {syncResult.Error}");
            return 1;
        }

        // Already-installed/already-current short-circuit (2026-09-01, Phase 5 recheck) - fresh-
        // install mode is invoked unconditionally by iYamPOS's button on every click, including
        // repeat clicks against a machine that's already up to date. Costs zero extra network calls -
        // LatestVersion already came back from the ResolveLatestPackageAsync call above, which
        // fresh-install mode was always going to make regardless. An unparseable version on either
        // side is treated as stale (proceed with the real install) rather than silently trusted -
        // never skip based on a comparison that couldn't actually be made. Downgrading an existing
        // install is already structurally impossible here since this branch only fires when the
        // existing version is >= latest.
        var existingExePath = InstallationDirectoryResolver.RelaunchExePath(targetDir);
        if (File.Exists(existingExePath))
        {
            // FileVersion, not ProductVersion (2026-09-01, recheck - real gap found live) - the .NET
            // SDK appends a "+<git-hash>" suffix to ProductVersion (via SourceLink/
            // InformationalVersion) for any project built inside a git repo, which Version.TryParse
            // can't parse at all - that silently defeated this whole check on every real build,
            // always falling into the safe-but-pointless "unparseable, proceed with update" path.
            // FileVersion stays purely numeric.
            var existingVersionString = FileVersionInfo.GetVersionInfo(existingExePath).FileVersion;
            if (Version.TryParse(existingVersionString, out var existingVersion) &&
                Version.TryParse(syncResult.LatestVersion, out var latestVersionParsed) &&
                existingVersion >= latestVersionParsed)
            {
                Console.WriteLine($"Already up to date (installed {existingVersion}, latest {latestVersionParsed}) - launching existing installation.");
                var launchedExisting = await new ProcessLauncher().LaunchAndConfirmAsync(existingExePath, TimeSpan.FromSeconds(3));
                return launchedExisting ? 0 : 1;
            }
        }

        packageUrl = syncResult.PackageUrl;
        packageSha256 = syncResult.PackageSha256;
        latestVersion = syncResult.LatestVersion;
    }
    else
    {
        Require(args_, "url", "sha256", "relaunch");
        targetDir = InstallationDirectoryResolver.ResolveForUpdate(args_["relaunch"]);
        Console.WriteLine($"Update mode. Target directory: {targetDir}");

        packageUrl = args_["url"];
        packageSha256 = args_["sha256"];
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    return 1;
}

using var instanceLock = InstanceLock.TryAcquire(targetDir);
if (!instanceLock.Acquired)
{
    Console.Error.WriteLine("Another NexusBootstrap instance is already running against this installation.");
    return 1;
}

var replacer = new InstallationReplacer();

// Crash recovery (2026-09-01) - if a PREVIOUS Bootstrap run left a PendingUpdate marker (e.g. the
// machine lost power between replacing files and confirming the new version launched), resolve it
// now, before attempting anything new. Without this, an interrupted update is invisible until a
// confused support call.
var leftoverState = UpdateState.TryRead(targetDir);
if (leftoverState is { PendingUpdate: true })
{
    Console.WriteLine($"Found an incomplete update from a previous run (target was {leftoverState.TargetVersion}, started {leftoverState.StartedAtUtc:u}) - rolling back.");
    if (Directory.Exists(InstallationReplacer.PreviousDir(targetDir)))
    {
        replacer.Rollback(targetDir);
        Console.WriteLine("Rollback complete.");
    }
    UpdateState.Delete(targetDir);
}

using var httpClientForDownload = new HttpClient();
var downloader = new PackageDownloader(httpClientForDownload);
var preflight = new PreflightChecker(new DiskSpaceChecker());
var launcher = new ProcessLauncher();

Console.WriteLine("Downloading and verifying package...");
var download = await downloader.DownloadAndVerifyAsync(packageUrl!, packageSha256!);
if (!download.Success)
{
    Console.Error.WriteLine(download.Error);
    return 1;
}

try
{
    var sizeCheck = preflight.CheckDiskSpace(targetDir, new FileInfo(download.TempZipPath!).Length);
    if (!sizeCheck.Passed)
    {
        Console.Error.WriteLine(sizeCheck.Error);
        return 1;
    }

    // Only relevant in Update mode - fresh-install has no running NexusBeta to wait for.
    if (!args_.ContainsKey("fresh-install"))
    {
        var exitCheck = await preflight.CheckProcessExitedAsync("NexusBeta", TimeSpan.FromSeconds(30));
        if (!exitCheck.Passed)
        {
            Console.Error.WriteLine(exitCheck.Error);
            return 1;
        }
    }

    var writableCheck = preflight.CheckTargetWritable(targetDir);
    if (!writableCheck.Passed)
    {
        Console.Error.WriteLine(writableCheck.Error);
        return 1;
    }

    string previousVersion = "none";
    var currentExePath = InstallationDirectoryResolver.RelaunchExePath(targetDir);
    if (File.Exists(currentExePath))
    {
        previousVersion = FileVersionInfo.GetVersionInfo(currentExePath).FileVersion ?? "unknown";
    }

    // State marker written BEFORE backup/replace starts (2026-09-01) - a crash anywhere from here
    // through relaunch confirmation is recoverable on the next Bootstrap run.
    new UpdateState
    {
        PendingUpdate = true,
        PreviousVersion = previousVersion,
        TargetVersion = latestVersion ?? "unknown",
        StartedAtUtc = DateTime.UtcNow,
    }.Write(targetDir);

    Console.WriteLine("Backing up current installation...");
    replacer.BackupCurrent(targetDir);

    Console.WriteLine("Installing new version...");
    replacer.ExtractNewVersion(download.TempZipPath!, targetDir);

    Console.WriteLine("Launching new version...");
    bool launchedOk = await launcher.LaunchAndConfirmAsync(currentExePath, TimeSpan.FromSeconds(10));

    if (launchedOk)
    {
        replacer.Commit(targetDir);
        UpdateState.Delete(targetDir);
        Console.WriteLine("Update complete.");
        return 0;
    }
    else
    {
        Console.Error.WriteLine("The new version failed to stay running - rolling back to the previous version.");
        replacer.Rollback(targetDir);
        UpdateState.Delete(targetDir);

        // Leave the user with a working app rather than nothing, even though this update failed.
        await launcher.LaunchAndConfirmAsync(currentExePath, TimeSpan.FromSeconds(1));
        return 1;
    }
}
catch (Exception ex)
{
    // Real gap closed on recheck (2026-09-01) - without this catch, an exception thrown mid
    // backup/extract/launch (e.g. a permissions error, disk filling up despite the pre-flight
    // check having just passed) would crash unhandled, leaving PendingUpdate=true for the NEXT
    // Bootstrap run to clean up rather than resolving it in THIS run. The next-run recovery path
    // still exists as a safety net (see the top of this file), but attempting the rollback
    // immediately here gives a cleaner error and a working app sooner.
    Console.Error.WriteLine($"Update failed: {ex.Message}");
    if (Directory.Exists(InstallationReplacer.PreviousDir(targetDir)))
    {
        try
        {
            replacer.Rollback(targetDir);
            UpdateState.Delete(targetDir);
            Console.Error.WriteLine("Rolled back to the previous version.");
        }
        catch (Exception rollbackEx)
        {
            // Marker deliberately NOT deleted here - rollback itself failed, so PendingUpdate
            // staying true is correct: it's still a genuinely incomplete update, and the next
            // Bootstrap run's own recovery path is the real safety net for this case.
            Console.Error.WriteLine($"Rollback also failed: {rollbackEx.Message}. The next Bootstrap run will retry recovery from the update-state.json marker.");
        }
    }
    return 1;
}
finally
{
    if (File.Exists(download.TempZipPath))
    {
        try { File.Delete(download.TempZipPath!); } catch { /* best-effort temp cleanup */ }
    }
}

static Dictionary<string, string> ParseArgs(string[] rawArgs)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < rawArgs.Length; i++)
    {
        if (!rawArgs[i].StartsWith("--"))
            continue;

        var key = rawArgs[i][2..];
        bool hasValue = i + 1 < rawArgs.Length && !rawArgs[i + 1].StartsWith("--");
        result[key] = hasValue ? rawArgs[++i] : "true";
    }
    return result;
}

static void Require(Dictionary<string, string> parsed, params string[] keys)
{
    var missing = keys.Where(k => !parsed.ContainsKey(k)).ToList();
    if (missing.Count > 0)
        throw new ArgumentException($"Missing required argument(s): {string.Join(", ", missing.Select(k => "--" + k))}");
}