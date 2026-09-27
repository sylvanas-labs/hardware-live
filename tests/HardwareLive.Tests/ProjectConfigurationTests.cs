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
