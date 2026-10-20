using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusBootstrap.Restore;

// This PC's identity as the Console knows it (restore plan v2, V2.4 Bootstrap step 2): NexusBeta's own activation.dat
// (NexusCloudClient LocalActivationWriter - DPAPI LocalMachine, %LocalAppData%\NexusPros\NexusBeta, the Windows user
// that runs NexusBeta). DeviceId is whatever NexusBeta uses today (disk serial while iYamPOS is installed, MachineGuid
// otherwise) - never re-derived here. Only the fields the restore needs; no reference to NexusCloudClient.
internal sealed class Activation
{
    [JsonPropertyName("CloudRecordHDDserial")]
    public string? DeviceId { get; set; }
    public string? ContractSerial { get; set; }
    public Guid OrganizationId { get; set; }
    public int BranchId { get; set; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(ContractSerial)
                              && OrganizationId != Guid.Empty && BranchId > 0;
}

internal static class ActivationReader
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPros", "NexusBeta", "activation.dat");

    // Null = missing, not readable on this PC / for this Windows user, or incomplete.
    public static Activation? TryLoad()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), optionalEntropy: null, DataProtectionScope.LocalMachine);
            var activation = JsonSerializer.Deserialize<Activation>(Encoding.UTF8.GetString(plain), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return activation is { IsComplete: true } ? activation : null;
        }
        catch
        {
            return null;
        }
    }
}
