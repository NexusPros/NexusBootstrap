using Npgsql;
using NexusDbShared;
using NexusDbShared.Restore;
using NexusDbShared.TenantSafety.Domain;
using NexusDbShared.TenantSafety.Rules;
using NexusPostgresProvisioning;
using NexusPostgresRestore.Sets;
using NexusPostgresRestore.Models;

namespace NexusBootstrap.Safety;

// `NexusBootstrap --pre-reset-backup --org <guid> --branch <int>` (R0, 2026-11-04). Called by iYamPOS's NexusBeta preparation right
// before a data reset: writes ONE verified backup set of both databases (purpose BeforeReset) with the shared LocalBackupSetWriter into
// the machine's NexusBeta backup folder (never a command-line path), so an accidental reset can be undone with NexusBeta's restore
// wizard (local set + Console approval). Local only, like backup and restore. Checks before writing: arguments, no open restore, the
// NexusBeta activation (when present) and the stamped database identities (when present) name the same organization / branch; the
// writer's own tenant check refuses other organizations' data. Exit codes: PreResetBackupRules; 0 only with a verified set, followed by
// one RESULT line on stdout. The database password stays in the child process environment (never on a command line).
internal static class PreResetBackupCommand
{
    public static async Task<int> RunAsync(Dictionary<string, string> args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            var argProblem = PreResetBackupRules.CheckArguments(args, out var organizationId, out var branchId);
            if (argProblem != null) return Fail(PreResetBackupRules.InvalidArguments, argProblem);

            if (RestoreGuard.IsRestoreOpen())
                return Fail(PreResetBackupRules.Busy, "A NexusBeta restore is open on this PC - finish it first.");

            var activation = Restore.ActivationReader.TryLoad();
            if (activation != null && (activation.OrganizationId != organizationId || activation.BranchId != branchId))
                return Fail(PreResetBackupRules.IdentityRefused, "NexusBeta on this PC is activated for another organization / branch.");

            // Same login the preparation uses: the per-install role when provisioned, else the legacy defaults.
            var credential = PostgresCredentialManager.TryLoad();
            if (credential != null)
            {
                NexusDbConn.DbUser = credential.Username;
                NexusDbConn.DbUserPassword = credential.Password;
            }
            var orgConnection = new NpgsqlConnectionStringBuilder(NexusDbConn.NexusOrganizationDbConnection);
            var branchConnection = new NpgsqlConnectionStringBuilder(NexusDbConn.NexusBranchDbConnection);

            var identityProblem = await CheckIdentityAsync(orgConnection, TenantSafetyRules.KindOrganization, organizationId, branchId, cts.Token)
                                  ?? await CheckIdentityAsync(branchConnection, TenantSafetyRules.KindBranch, organizationId, branchId, cts.Token);
            if (identityProblem != null) return Fail(PreResetBackupRules.IdentityRefused, identityProblem);

            var folder = BackupFolders.Effective(BackupFolders.ReadConfiguredFolder());
            Console.WriteLine("Backup folder: " + folder);
            var progress = new Progress<BackupWriteProgress>(p => Console.WriteLine($"{p.Stage} {p.Percent}% {p.Detail}"));
            var written = await LocalBackupSetWriter.CreateAsync(new BackupWriteRequest(folder, organizationId, branchId,
                "NexusBootstrap " + typeof(BootstrapEndpoints).Assembly.GetName().Version, orgConnection, branchConnection,
                Purpose: BackupPurposes.BeforeReset), progress, cts.Token);
            if (!written.Ok)
                return Fail(PreResetBackupRules.ExitCodeFor(written.Failure), written.Error ?? written.Failure.ToString());

            // The same checks the restore runs on this set (hashes, archive entries, one org + one branch, Local, migration ids).
            var problems = await BackupSetVerifier.VerifyAsync(written.SetFolder!, written.ServerMajor, organizationId, branchId, null, null, cts.Token);
            if (problems.Count > 0)
                return Fail(PreResetBackupRules.VerifyFailed, "The backup failed its check: " + string.Join("; ", problems.Select(p => p.Code + " " + p.Message)));

            var manifest = BackupSetFolders.ReadManifest(written.SetFolder!);
            if (manifest == null) return Fail(PreResetBackupRules.VerifyFailed, "The backup's manifest can't be read back.");
            var manifestSha = await LocalBackupSetWriter.HashFileAsync(Path.Combine(written.SetFolder!, BackupSetFolders.ManifestFileName), cts.Token);
            Console.WriteLine(PreResetBackupRules.FormatResult(PreResetBackupRules.Ok, manifestSha,
                PreResetBackupRules.BusinessRows(manifest.Member(BackupManifest.OrganizationRole)),
                PreResetBackupRules.BusinessRows(manifest.Member(BackupManifest.BranchRole)), written.SetFolder!));
            return PreResetBackupRules.Ok;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return Fail(PreResetBackupRules.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(PreResetBackupRules.Unexpected, ex.Message);
        }
    }

    // A stamped identity must match (Local only: a shared Organization database is refused). No identity table / row yet = the
    // writer's own tenant check and the reset's checks decide.
    private static async Task<string?> CheckIdentityAsync(NpgsqlConnectionStringBuilder connection, int kind, Guid organizationId, int branchId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connection.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('public.\"DatabaseIdentities\"') IS NOT NULL", conn))
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!) return null;

        await using var cmd = new NpgsqlCommand("SELECT \"DatabaseKind\", \"OrganizationId\", \"BranchId\" FROM \"DatabaseIdentities\" LIMIT 1", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var row = new IdentityRow { DatabaseKind = r.GetInt32(0), OrganizationId = r.GetGuid(1), BranchId = r.GetInt32(2) };
        var decision = TenantSafetyRules.DecideIdentity(kind, organizationId, branchId, row, null);
        return decision.Value == TenantSafetyRules.IdentityCheck.Match ? null : decision.Reason;
    }

    private sealed class IdentityRow : DatabaseIdentityBase { }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine(message);
        Console.WriteLine($"{PreResetBackupRules.ResultPrefix}{exitCode}");
        return exitCode;
    }
}
