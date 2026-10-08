using System.Diagnostics;
using NexusPostgresProvisioning;

namespace NexusBootstrap.Services;

public record PreflightResult(bool Passed, string? Error);

// CI/CD Phase 4 (2026-09-01, review finding - "the difference between a clean MVP and a support
// nightmare") - none of these checks existed in the original sketch. Runs AFTER the download+hash
// verification (never before - no point checking disk space for a package we might reject anyway),
// but BEFORE any file is touched in the target directory.
public class PreflightChecker
{
    private readonly IDiskSpaceChecker _diskSpaceChecker;

    public PreflightChecker(IDiskSpaceChecker diskSpaceChecker)
    {
        _diskSpaceChecker = diskSpaceChecker;
    }

    public PreflightResult CheckDiskSpace(string targetDir, long packageSizeBytes)
    {
        // Headroom beyond the raw package size - the replace step keeps a full copy of the
        // previous version in previous/ during the operation (for rollback), so real usage is
        // closer to 2x the package size, not 1x.
        long required = packageSizeBytes * 2 + 100_000_000;
        long available = _diskSpaceChecker.GetAvailableBytes(targetDir);

        return available >= required
            ? new PreflightResult(true, null)
            : new PreflightResult(false, $"Insufficient disk space: {available:N0} bytes available, {required:N0} required.");
    }

    // Never force-kills or force-overwrites - if NexusBeta hasn't exited after a reasonable wait,
    // this returns a controlled failure so the caller can retry later, rather than risking a
    // partial write against open files.
    public async Task<PreflightResult> CheckProcessExitedAsync(string processName, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName(processName).Length == 0)
                return new PreflightResult(true, null);

            await Task.Delay(500, ct);
        }

        return Process.GetProcessesByName(processName).Length == 0
            ? new PreflightResult(true, null)
            : new PreflightResult(false, $"'{processName}' is still running after {timeout.TotalSeconds:0}s - refusing to replace files while it may still have them open.");
    }

    public PreflightResult CheckTargetWritable(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            var probePath = Path.Combine(targetDir, $".nexusbootstrap-writetest-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
            return new PreflightResult(true, null);
        }
        catch (Exception ex)
        {
            return new PreflightResult(false, $"Target directory is not writable: {ex.Message}");
        }
    }

    // Thin wrapper (2026-09-04, moved from here) - the actual detect/repair/migrate/onboard decision
    // logic now lives in NexusPostgresProvisioning.PostgresProvisioningOrchestrator, shared with
    // iYamPOS (net48 can't reference this net10.0 project, so the logic itself had to live somewhere
    // both could reach - see that class's own comment for the full story). This method's only job
    // now is supplying the console-specific "ask the user something" implementation and translating
    // the shared ProvisioningResult into Bootstrap's own PreflightResult shape. Still the deliberate,
    // explicit exception to this project's own "database access belongs in NexusBeta/Console, never
    // here" charter (see Program.cs's header) - installing PostgreSQL is install-time infrastructure
    // setup, not the runtime querying/CRUD the charter is actually guarding against.
    public async Task<PreflightResult> CheckPostgresProvisionedAsync(string installationId, Action<string>? progress = null)
    {
        // Outer safety net (real gap found on review, 2026-09-04) - every path inside the
        // orchestrator already wraps its own Postgres/file-I/O work in try/catch, but this call site
        // itself had none. Program.cs's own top-level try/catch doesn't cover this call (it only
        // wraps the earlier args-parsing/package-resolution section), so an unexpected exception
        // here would have crashed Bootstrap with a raw stack trace instead of the clean "return 1
        // with a message" every other preflight check in this class already guarantees.
        try
        {
            var orchestrator = new PostgresProvisioningOrchestrator(new ConsolePrompt());
            var result = await orchestrator.EnsureProvisionedAsync(installationId, progress);
            return new PreflightResult(result.Success, result.Error);
        }
        catch (Exception ex)
        {
            return new PreflightResult(false, $"Unexpected error checking PostgreSQL: {ex.Message}");
        }
    }
}

// Console.Write/ReadKey-based IProvisioningPrompt - safe here specifically because NexusBootstrap is
// a real console app (confirmed: iYamPOS launches it via ProcessStartInfo.UseShellExecute = true
// with no CreateNoWindow, which spawns a real, visible, interactive console). iYamPOS itself cannot
// reuse this implementation - see its own WinForms-based IProvisioningPrompt implementation instead.
public class ConsolePrompt : IProvisioningPrompt
{
    public string AskSuperuserPassword(string reason)
    {
        Console.Write($"Enter the password for {reason}: ");
        return ReadPasswordMasked();
    }

    public bool AskCreateDedicatedDatabase()
    {
        Console.WriteLine();
        Console.WriteLine("PostgreSQL is installed on this machine, but Nexus has no working credential for it.");
        Console.WriteLine("  1) I know the PostgreSQL superuser (postgres) password - let Nexus create its own dedicated database and role (recommended)");
        Console.WriteLine("  2) I already have a login Nexus should use directly against the existing NexusOrganization/NexusBranch databases");
        Console.Write("Choose 1 or 2: ");
        string? choice = Console.ReadLine()?.Trim();
        return choice != "2";
    }

    public (string Username, string Password) AskExistingLogin()
    {
        Console.Write("Username: ");
        string? username = Console.ReadLine();

        Console.Write("Password: ");
        string password = ReadPasswordMasked();

        return (username ?? string.Empty, password);
    }

    private static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return password.ToString();
    }
}

// Simple file-lock guard against a second Bootstrap instance running concurrently against the same
// target directory (e.g. a user double-clicking "Update Now" twice). Not a distributed lock - this
// only needs to work for "two processes on the same machine, same target folder," which a plain
// exclusive-open file handle already solves.
public class InstanceLock : IDisposable
{
    private readonly FileStream? _lockStream;

    private InstanceLock(FileStream? lockStream)
    {
        _lockStream = lockStream;
    }

    public bool Acquired => _lockStream != null;

    public static InstanceLock TryAcquire(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            var path = Path.Combine(targetDir, ".nexusbootstrap.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            // Already held by another Bootstrap instance.
            return new InstanceLock(null);
        }
    }

    public void Dispose() => _lockStream?.Dispose();
}