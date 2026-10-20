using NexusDbShared;
using NexusDbShared.Restore;
using NexusPostgresProvisioning;
using NexusPostgresRestore;
using NexusPostgresRestore.Local;

namespace NexusBootstrap.Restore;

// `NexusBootstrap.exe --restore-check` (2026-10-20, restore plan v2 V2.3 / V2.4 step 2). Read-only: can this PC's
// Organization + Branch databases be restored in place by the app role, without the postgres superuser? NexusBeta runs
// it before asking SuperAdmin for an approval, and every restore runs the same check before touching anything.
// Output: "RESULT OK" or one "PROBLEM <code> <message>" line per problem (NexusBeta reads these); never a password.
// Exit codes: 0 = OK, 3 = support needed.
internal static class RestoreCheckCommand
{
    public const int SupportNeeded = 3;

    public static async Task<int> RunAsync()
    {
        if (RestoreGuard.IsRestoreOpen())
            return Fail("RestoreOpen", "A restore is already open on this PC - it must be finished (recovery) first.");

        // The app role's credential from the provisioning store - the same login NexusBeta uses. Never the superuser.
        var credential = PostgresCredentialManager.TryLoad();
        if (credential == null)
            return Fail("NoCredential", "No stored Nexus database login on this PC for this Windows user.");

        var login = new PgLogin(credential.Host, credential.Port, credential.Username, credential.Password, credential.SslMode);
        var result = await LocalRestorePrecheck.RunAsync(login, [NexusDbConn.OrganizationDbName, NexusDbConn.BranchDbName]);

        Console.WriteLine($"PostgreSQL {result.ServerVersion ?? "unknown"}; tools: {result.ToolsFolder ?? "not found"}");
        if (result.Ok)
        {
            Console.WriteLine("RESULT OK");
            return 0;
        }
        foreach (var problem in result.Problems)
            Console.WriteLine($"PROBLEM {problem.Code} {problem.Message}");
        return SupportNeeded;
    }

    private static int Fail(string code, string message)
    {
        Console.WriteLine($"PROBLEM {code} {message}");
        return SupportNeeded;
    }
}
