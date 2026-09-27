using System.Xml.Linq;

namespace HardwareLive.Tests;

public sealed class ProjectConfigurationTests
{
    [Fact]
    public void AppProjectUsesWinExeOutputType()
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "src", "HardwareLive.App", "HardwareLive.App.csproj");
        var project = XDocument.Load(projectPath);

        var outputType = project
            .Descendants("OutputType")
            .Select(element => element.Value)
            .Single();

        Assert.Equal("WinExe", outputType);
    }

    [Fact]
    public void SamplerProjectUsesWinExeOutputType()
    {
        var project = LoadProject("src", "HardwareLive.Sampler", "HardwareLive.Sampler.csproj");

        Assert.Equal("WinExe", project.Descendants("OutputType").Select(element => element.Value).Single());
    }

    [Theory]
    [InlineData("src", "HardwareLive.App", "HardwareLive.App.csproj")]
    [InlineData("src", "HardwareLive.Core", "HardwareLive.Core.csproj")]
    [InlineData("src", "HardwareLive.Sampler", "HardwareLive.Sampler.csproj")]
    [InlineData("tests", "HardwareLive.Tests", "HardwareLive.Tests.csproj")]
    public void WindowsProjectsTargetNetTenWindows(params string[] path)
    {
        var project = LoadProject(path);

        Assert.Equal("net10.0-windows", project.Descendants("TargetFramework").Select(element => element.Value).Single());
    }

    [Fact]
    public void ProtocolTargetsPortableNetTen()
    {
        var project = LoadProject("src", "HardwareLive.Protocol", "HardwareLive.Protocol.csproj");

        Assert.Equal("net10.0", project.Descendants("TargetFramework").Select(element => element.Value).Single());
    }

    private static XDocument LoadProject(params string[] relativePath) =>
        XDocument.Load(Path.Combine([FindRepositoryRoot(), .. relativePath]));

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
