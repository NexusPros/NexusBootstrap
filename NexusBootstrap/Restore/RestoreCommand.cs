using System.Diagnostics;
using System.Text.RegularExpressions;
using NexusDbShared;
using NexusDbShared.Restore;
using NexusPostgresProvisioning;
using NexusPostgresRestore;
using NexusPostgresRestore.Local;
using NexusPostgresRestore.Models;

namespace NexusBootstrap.Restore;

// NexusBeta restore (2026-10-20, restore plan v2, V2.4 / V2.5):
//   --restore --request <id> --job <id> --set <folder> --safety <folder> --relaunch <NexusBeta.exe> [--undo]
//       started by NexusBeta after SuperAdmin approved; the client key arrives on standard input (one line, 64 hex).
//   --restore-recover [--relaunch <NexusBeta.exe>]
//       finishes or rolls back an open restore (NexusBeta starts it when it finds an open journal); works offline.
// Output for NexusBeta: "RESULT <exit code> <state>", "PROBLEM <code> <message>" lines, "MESSAGE <text>"; the same lines
// go to %ProgramData%\Nexus\NexusBeta\restore\logs\restore-<time>.log. Never a password, token or key.
// Exit codes: JobOutcome (0 restored / nothing open, 1 not done - databases as before, 3 support needed, 4 apps open,
// 5 PostgreSQL not reachable now).
internal static class RestoreCommand
{
    private static readonly Regex KeyFormat = new("^[0-9a-fA-F]{64}$");

    public static async Task<int> RunAsync(Dictionary<string, string> args, bool recoverOnly)
    {
        var log = new List<string>();
        void Write(string line)
        {
            Console.WriteLine(line);
            log.Add(line);
        }

        JobOutcome outcome;
        try
        {
            outcome = await RunCoreAsync(args, recoverOnly);
        }
        catch (Exception ex)
        {
            // An unexpected stop: the journal (if any) is exactly as last saved, so the next recovery continues safely.
            outcome = new JobOutcome(JobOutcome.SupportNeeded, null, [new("Unexpected", ex.Message)], "The restore stopped unexpectedly - it continues on the next start.");
        }

        Write($"RESULT {outcome.ExitCode} {outcome.State?.ToString() ?? "-"}");
        foreach (var problem in outcome.Problems)
            Write($"PROBLEM {problem.Code} {problem.Message}");
        Write("MESSAGE " + outcome.Message);
        SaveLog(log);

        // NexusBeta decides what to show (its start guard reads the journal) - except when it is still open itself.
        if (outcome.ExitCode != JobOutcome.AppsOpen && args.TryGetValue("relaunch", out var relaunch))
            Relaunch(relaunch);
        return outcome.ExitCode;
    }

    private static async Task<JobOutcome> RunCoreAsync(Dictionary<string, string> args, bool recoverOnly)
    {
        var credential = PostgresCredentialManager.TryLoad();
        if (credential == null)
            return Fail(JobOutcome.SupportNeeded, "NoCredential", "No stored Nexus database login on this PC for this Windows user.");
        var login = new PgLogin(credential.Host, credential.Port, credential.Username, credential.Password, credential.SslMode);

        // The key: required to start, optional for recovery (reports without it wait in the outbox for NexusBeta).
        var key = ReadKeyFromStdin();
        if (!recoverOnly && key == null)
            return Fail(JobOutcome.NotDone, "NoKey", "A restore can only be started from NexusBeta.");

        var activation = ActivationReader.TryLoad();
        if (!recoverOnly && activation == null)
            return Fail(JobOutcome.SupportNeeded, "NoActivation", "This PC's NexusBeta activation can't be read for this Windows user.");

        using var http = new HttpClient { BaseAddress = new Uri(BootstrapEndpoints.ConsoleBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        var console = activation != null ? new ConsoleRestoreClient(http, activation, key) : (NexusPostgresRestore.Recovery.IRestoreConsole)new OfflineConsole();
        var job = new LocalRestoreJob(login, new JournalStore(), console, Path.Combine(RestoreGuard.RestoreFolder, "work"), () => DateTime.UtcNow);

        if (recoverOnly)
            return await job.RecoverAsync();

        if (!Guid.TryParse(args.GetValueOrDefault("request"), out var requestId) || !Guid.TryParse(args.GetValueOrDefault("job"), out var jobId)
            || !args.TryGetValue("set", out var setFolder) || !args.TryGetValue("safety", out var safetyFolder))
            return Fail(JobOutcome.NotDone, "BadArguments", "Missing --request, --job, --set or --safety.");

        return await job.StartAsync(new RestoreJobRequest(
            requestId, jobId, args.ContainsKey("undo") ? RestorePurpose.Undo : RestorePurpose.Restore,
            Path.GetFullPath(setFolder), Path.GetFullPath(safetyFolder),
            activation!.OrganizationId, activation.BranchId, NexusDbConn.OrganizationDbName, NexusDbConn.BranchDbName));
    }

    // One line on standard input, only when it is redirected (never a prompt on a console).
    private static string? ReadKeyFromStdin()
    {
        if (!Console.IsInputRedirected) return null;
        var line = Console.In.ReadLine()?.Trim();
        return line != null && KeyFormat.IsMatch(line) ? line.ToLowerInvariant() : null;
    }

    private static void Relaunch(string exe)
    {
        try
        {
            if (!File.Exists(exe) || !Path.GetFileName(exe).Equals("NexusBeta.exe", StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch
        {
            // The user starts NexusBeta; its start guard continues the restore.
        }
    }

    private static void SaveLog(List<string> lines)
    {
        try
        {
            var folder = Path.Combine(RestoreGuard.RestoreFolder, "logs");
            MachineFolders.Ensure(folder);
            File.WriteAllLines(Path.Combine(folder, $"restore-{DateTime.Now:yyyyMMdd-HHmmss}.log"), lines);
        }
        catch
        {
            // the console output already carries the same lines
        }
    }

    private static JobOutcome Fail(int exitCode, string code, string message) => new(exitCode, null, [new(code, message)], message);

    // Recovery without activation (e.g. another Windows user): reports stay in the outbox.
    private sealed class OfflineConsole : NexusPostgresRestore.Recovery.IRestoreConsole
    {
        public Task<RestoreGrant?> GetGrantAsync(Guid requestId, Guid jobId, CancellationToken ct) => Task.FromResult<RestoreGrant?>(null);

        public Task<(NexusPostgresRestore.Recovery.ReportAck Ack, string? Message)> ReportAsync(RestoreJournal journal, OutboxReport report, CancellationToken ct)
            => Task.FromResult<(NexusPostgresRestore.Recovery.ReportAck, string?)>((NexusPostgresRestore.Recovery.ReportAck.Unreachable, null));
    }
}
