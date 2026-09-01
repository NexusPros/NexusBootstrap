using System.IO.Compression;

namespace NexusBootstrap.Services;

// CI/CD Phase 4 (2026-09-01) - the actual file-replace step, with rollback built in from the start
// (review finding: "don't make rollback impossible"). Reserved names inside targetDir that are
// never touched by backup/restore: the "previous" folder itself, the update-state.json marker, and
// the instance lock file - everything else in targetDir's root is treated as "the app files."
//
// SAFETY ASSUMPTION this whole class depends on: targetDir contains ONLY the app's own binaries,
// never user data - Commit() deletes the "previous" backup outright on success, which would mean
// permanent data loss for anything else living there. This holds by construction: NexusBeta's own
// user data (activation.dat, ProfileImages, sales-drafts) already lives under MAUI's
// FileSystem.AppDataDirectory, a genuinely different folder from wherever the exe/dlls sit - and
// InstallationDirectoryResolver.ResolveForFreshInstall() deliberately puts the app in its own "app"
// subfolder, separate from that data folder, for exactly this reason. Never point targetDir at a
// folder that also holds anything worth keeping.
public class InstallationReplacer
{
    private const string PreviousFolderName = "previous";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        PreviousFolderName,
        "update-state.json",
        ".nexusbootstrap.lock",
    };

    public static string PreviousDir(string targetDir) => Path.Combine(targetDir, PreviousFolderName);

    // Moves the current app files aside rather than deleting them outright - this is what makes
    // rollback possible at all. Called BEFORE extraction, after UpdateState.Write has already
    // persisted the marker (see Program.cs's orchestration) so a crash between these two steps is
    // still recoverable on the next run.
    public void BackupCurrent(string targetDir)
    {
        var previousDir = PreviousDir(targetDir);
        if (Directory.Exists(previousDir))
            Directory.Delete(previousDir, recursive: true);
        Directory.CreateDirectory(previousDir);

        foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir))
        {
            var name = Path.GetFileName(entry);
            if (ReservedNames.Contains(name))
                continue;

            var destination = Path.Combine(previousDir, name);
            if (Directory.Exists(entry))
                Directory.Move(entry, destination);
            else
                File.Move(entry, destination);
        }
    }

    public void ExtractNewVersion(string zipPath, string targetDir)
    {
        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
    }

    // Deletes whatever's currently in targetDir (the bad new version that failed to launch), then
    // restores everything from previous/ back into place. Reserved names (the marker, the lock)
    // survive this - the caller clears the marker itself once rollback completes successfully.
    public void Rollback(string targetDir)
    {
        var previousDir = PreviousDir(targetDir);
        if (!Directory.Exists(previousDir))
            throw new InvalidOperationException($"Cannot roll back - no '{PreviousFolderName}' backup exists at '{targetDir}'.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir))
        {
            var name = Path.GetFileName(entry);
            if (ReservedNames.Contains(name))
                continue;

            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(previousDir))
        {
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(targetDir, name);
            if (Directory.Exists(entry))
                Directory.Move(entry, destination);
            else
                File.Move(entry, destination);
        }

        Directory.Delete(previousDir, recursive: true);
    }

    // Successful update - the backup is no longer needed. Deliberately deletes it (frees disk
    // space) rather than keeping it around indefinitely - matches a normal installer's behavior;
    // this isn't meant to be a multi-version history.
    public void Commit(string targetDir)
    {
        var previousDir = PreviousDir(targetDir);
        if (Directory.Exists(previousDir))
            Directory.Delete(previousDir, recursive: true);
    }
}