using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NexusBootstrap.Services;

// Package integrity check (2026-10-12, real pilot failure). NexusBeta 1.0.1 shipped with an OLDER
// NexusDbShared.dll (1.0.0, overwritten by release.yml's Bootstrap copy step) than NexusBeta.dll was
// compiled against (1.0.1) - .NET refuses to load a lower assembly version, so the app died silently
// (0xC000027B, no log) on every machine. Nothing caught it: CI never opened the final zip and
// Bootstrap's --no-launch install only compared NexusBeta.exe's FileVersion.
//
// One implementation, two callers:
//   - release.yml runs "NexusBootstrap.exe --verify-package <extracted final zip> --expect-version <tag>"
//     against the exact artifact it uploads, and fails the release on any problem;
//   - Program.cs runs it on every freshly extracted install BEFORE committing (deleting the previous
//     version) or launching - the one check that is safe before NexusBeta's database is prepared.
//
// Rules (top-level files only - the app folder is flat, satellite resource folders are not assemblies
// anything references by version):
//   1. every managed assembly's reference to an assembly that IS in the folder must be satisfied by an
//      equal-or-higher version (the exact rule the .NET runtime enforces at load time);
//   2. a reference to one of our own assemblies (Nexus*) that is NOT in the folder is a problem;
//   3. with an expected version, NexusBeta.dll and NexusDbShared.dll FileVersions must match it.
public static class PackageIntegrityChecker
{
    public record Result(bool Passed, IReadOnlyList<string> Problems, int AssembliesChecked, IReadOnlyList<string> Notes);

    private static readonly string[] VersionStampedAssemblies = { "NexusBeta.dll", "NexusDbShared.dll" };

    // WPF ("Microsoft.WindowsDesktop.App") framework assemblies. NexusBeta is WinUI and does not ship WPF -
    // the package only carries .NET's 4.0.0.0 compatibility stand-ins. A library may still reference the
    // real ones for helpers NexusBeta never calls (seen 2026-10-12: ZXing.Windows.Compatibility's WPF
    // bitmap helpers -> WindowsBase 10.0; Beta uses only its System.Drawing reader). .NET resolves a
    // reference only when that code runs, so these are reported as notes, not package defects.
    private static readonly HashSet<string> WpfFrameworkAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsBase", "PresentationCore", "PresentationFramework", "System.Xaml",
    };

    public static Result Check(string folder, string? expectedVersion)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        if (!Directory.Exists(folder))
            return new Result(false, new[] { $"Folder not found: {folder}" }, 0, notes);

        // name -> (version, file) for every managed assembly in the folder.
        var present = new Dictionary<string, (Version Version, string File)>(StringComparer.OrdinalIgnoreCase);
        var references = new List<(string File, string Name, Version Version)>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.dll").Concat(Directory.EnumerateFiles(folder, "*.exe")))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    continue; // native dll / apphost / single-file bundle

                var md = pe.GetMetadataReader();
                if (!md.IsAssembly)
                    continue;

                var def = md.GetAssemblyDefinition();
                present[md.GetString(def.Name)] = (def.Version, Path.GetFileName(path));

                foreach (var handle in md.AssemblyReferences)
                {
                    var r = md.GetAssemblyReference(handle);
                    references.Add((Path.GetFileName(path), md.GetString(r.Name), r.Version));
                }
            }
            catch (BadImageFormatException)
            {
                // not a PE file we can read - not an assembly anything binds to
            }
        }

        foreach (var (file, name, version) in references)
        {
            if (present.TryGetValue(name, out var found))
            {
                if (found.Version < version && WpfFrameworkAssemblies.Contains(name))
                    notes.Add($"{file} references WPF {name} {version} (package has the {found.Version} stand-in) - fine unless that WPF code is used.");
                else if (found.Version < version)
                    problems.Add($"{file} needs {name} {version}, but the package has {found.Version} ({found.File}) - .NET will refuse to load it.");
            }
            else if (name.StartsWith("Nexus", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file} needs {name} {version}, which is missing from the package.");
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedVersion))
        {
            if (!Version.TryParse(expectedVersion, out var expected))
            {
                problems.Add($"Expected version '{expectedVersion}' is not a plain x.y.z version.");
            }
            else
            {
                foreach (var stamped in VersionStampedAssemblies)
                {
                    var path = Path.Combine(folder, stamped);
                    if (!File.Exists(path))
                    {
                        problems.Add($"{stamped} is missing from the package.");
                        continue;
                    }
                    var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                    if (!Version.TryParse(fileVersion, out var actual) || Normalize(actual) != Normalize(expected))
                        problems.Add($"{stamped} has version {fileVersion ?? "none"}, expected {expectedVersion}.");
                }
            }
        }

        var distinct = problems.Distinct().ToList();
        return new Result(distinct.Count == 0, distinct, present.Count, notes.Distinct().ToList());
    }

    // 1.0.2 == 1.0.2.0
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
