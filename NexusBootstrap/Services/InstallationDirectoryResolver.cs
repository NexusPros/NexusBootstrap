namespace NexusBootstrap.Services;

// CI/CD Phase 4 (2026-09-01, review finding - real gap) - Bootstrap resolves its own installation
// directory; it never accepts one as a trusted caller-supplied path. A "--targetDir <path>"
// argument would be a value anything launching Bootstrap could supply, including a compromised/
// spoofed caller - treating "the caller says install here" as authoritative for a local filesystem
// write is exactly the mistake this class exists to avoid.
public static class InstallationDirectoryResolver
{
    // Update mode: the caller (NexusBeta, via NexusUpdateGate.razor) passes its OWN running
    // executable's path as a hint, not an instruction - this method verifies it actually looks like
    // a real, existing NexusBeta install before trusting it, rather than blindly obeying it.
    public static string ResolveForUpdate(string relaunchExePath)
    {
        if (string.IsNullOrWhiteSpace(relaunchExePath))
            throw new ArgumentException("A relaunch executable path is required for update mode.");

        if (!File.Exists(relaunchExePath))
            throw new InvalidOperationException($"'{relaunchExePath}' does not point at a real, existing file - refusing to treat it as an installation to update.");

        if (!Path.GetFileName(relaunchExePath).Equals("NexusBeta.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{relaunchExePath}' is not named NexusBeta.exe - refusing to treat an unrecognized executable as an installation to update.");

        var targetDir = Path.GetDirectoryName(Path.GetFullPath(relaunchExePath));
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            throw new InvalidOperationException($"Could not resolve a valid parent directory for '{relaunchExePath}'.");

        return targetDir;
    }

    // Fresh-install mode: a fixed, Bootstrap-owned default - never a caller-supplied string. Kept
    // under %LocalAppData% (per-user, no admin rights needed) rather than Program Files - matches
    // the same reasoning NexusClientLifecycle's own locked decisions already use for MSIX being the
    // eventual production path (unpackaged MAUI apps under Program Files have a known WebView2
    // cache/user-data bug). Deliberately a SEPARATE "app" subfolder from wherever NexusBeta's own
    // activation.dat lives (LocalActivationWriter's own folder) - a file-replace operation here
    // must never risk touching that file.
    public static string ResolveForFreshInstall()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NexusPros", "NexusBeta", "app");
    }

    public static string RelaunchExePath(string targetDir) => Path.Combine(targetDir, "NexusBeta.exe");
}