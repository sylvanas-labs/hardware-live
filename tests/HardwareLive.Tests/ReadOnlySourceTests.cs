using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace HardwareLive.Tests;

public sealed partial class ReadOnlySourceTests
{
    private static readonly string[] ForbiddenIdentifiers =
    [
        "IControl",
        "ISensor.Control",
        "SetSoftware",
        "SetDefault",
    ];

    // Matches a member-access on something literally named "Control" (e.g. `sensor.Control`,
    // `hardware.Control.SetSoftware(...)`), the shape an LHM write call takes. Deliberately
    // does NOT match ".FullControl"/".ReadAndExecute"-style identifiers that merely end in
    // "Control" (PipeAccessRights.FullControl, FileSystemRights.FullControl) or namespaces
    // like "System.Security.AccessControl"/".GetAccessControl()", because \. requires a dot
    // immediately before the literal "Control", and \b after it rejects a longer identifier
    // continuing past "Control" (so ".Controls" also doesn't match).
    [GeneratedRegex(@"\.Control\b")]
    private static partial Regex ControlMemberAccess();

    [Fact]
    public void ProductionSourceNeverReferencesHardwareWriteApis()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenIdentifiers)
            {
                Assert.DoesNotContain(forbidden, content, StringComparison.Ordinal);
            }

            var match = ControlMemberAccess().Match(content);
            Assert.False(match.Success, $"{file} contains a suspicious '.Control' member access: '{match.Value}'");
        }
    }

    /// <summary>
    /// Belt-and-braces beyond the text scan: opens the actual compiled hl-sampler.dll (the
    /// copy the test host runs against, via AppContext.BaseDirectory, not a possibly-stale
    /// bin/ artifact from another configuration) and fails if any type/member reference in
    /// its metadata names an LHM control API. This catches a write call reached only via
    /// reflection, an alias, or a using-alias that would dodge the source-text scan.
    /// </summary>
    [Fact]
    public void CompiledSamplerAssemblyNeverReferencesHardwareControlApis()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "hl-sampler.dll");
        Assert.True(File.Exists(dllPath), $"Expected the sampler assembly to be copied next to the test host at {dllPath}.");

        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.TypeReferences)
        {
            var typeReference = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(typeReference.Namespace);
            var name = metadata.GetString(typeReference.Name);
            Assert.False(
                ns == "LibreHardwareMonitor.Hardware" && name == "IControl",
                "hl-sampler.dll references LibreHardwareMonitor.Hardware.IControl.");
        }

        string[] forbiddenMemberNames = ["SetSoftware", "SetDefault", "get_Control"];
        foreach (var handle in metadata.MemberReferences)
        {
            var memberReference = metadata.GetMemberReference(handle);
            var name = metadata.GetString(memberReference.Name);
            Assert.DoesNotContain(name, forbiddenMemberNames);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HardwareLive.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Hardware Live repository root.");
    }
}
