namespace HardwareLive.App;

/// <summary>
/// Recognized command-line flags for the reopen path (docs/SPEC.md Component 8, install.ps1
/// step 5): <c>--no-window</c> suppresses the dashboard window on the primary instance;
/// <c>--open</c> asks for the window to be (re)opened, either right after this instance
/// starts serving, or -- if another instance already owns the named mutex -- by just
/// launching the browser and exiting. Unknown flags (such as <c>--port N</c>, handled by
/// <see cref="HardwareLive.Core.PortConfiguration"/>) are ignored here, not rejected.
/// </summary>
public static class AppArguments
{
    public readonly record struct Parsed(bool NoWindow, bool Open);

    public static Parsed Parse(IReadOnlyList<string> args)
    {
        var noWindow = false;
        var open = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--no-window", StringComparison.Ordinal))
            {
                noWindow = true;
            }
            else if (string.Equals(arg, "--open", StringComparison.Ordinal))
            {
                open = true;
            }
        }

        return new Parsed(noWindow, open);
    }
}
