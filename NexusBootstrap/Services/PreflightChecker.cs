using System.Diagnostics;

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