namespace NexusBootstrap.Services;

// CI/CD Phase 4 (2026-09-01, review finding) - a small injectable abstraction, not a direct static
// DriveInfo call, specifically so pre-flight disk-space verification can be tested with a fake
// low-space value rather than needing to actually exhaust a real test machine's disk.
public interface IDiskSpaceChecker
{
    long GetAvailableBytes(string path);
}

public class DiskSpaceChecker : IDiskSpaceChecker
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException($"Could not resolve a drive root for path '{path}'.");

        return new DriveInfo(root).AvailableFreeSpace;
    }
}