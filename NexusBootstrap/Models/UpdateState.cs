using System.Text.Json;

namespace NexusBootstrap.Models;

// CI/CD Phase 4 (2026-09-01) - the crash-recovery marker. Written BEFORE the replace step starts,
// deleted only after the new version is confirmed to have launched successfully. If Bootstrap (or
// the whole machine) dies mid-update, this file is the only way a later run can tell an update was
// left incomplete - without it, an interrupted update is invisible until a confused support call.
// Lives at <targetDir>\update-state.json - sits next to the app files, untouched by the zip
// extraction (NexusBeta's own build output never contains a file with this name).
public class UpdateState
{
    public bool PendingUpdate { get; set; }
    public string PreviousVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }

    private static string PathFor(string targetDir) => Path.Combine(targetDir, "update-state.json");

    public static UpdateState? TryRead(string targetDir)
    {
        var path = PathFor(targetDir);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path));
        }
        catch
        {
            // Corrupt/partial marker (e.g. the machine died mid-write of this very file) - treat as
            // no marker rather than crashing the next Bootstrap run over a diagnostic artifact.
            return null;
        }
    }

    public void Write(string targetDir)
    {
        File.WriteAllText(PathFor(targetDir), JsonSerializer.Serialize(this));
    }

    public static void Delete(string targetDir)
    {
        var path = PathFor(targetDir);
        if (File.Exists(path))
            File.Delete(path);
    }
}