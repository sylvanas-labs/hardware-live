using HardwareLive.App;
using HardwareLive.Core;
using HardwareLive.Core.Profiles;

// %LOCALAPPDATA%\HardwareLive\config.json takes priority (the app runs unelevated and
// Program Files is read-only); the app-base-directory file is a backward-compatible
// fallback for installs that predate this location.
var configPath = ConfigPaths.Resolve(AppContext.BaseDirectory);

// Installer security fix: an elevated install.ps1 run never writes %LOCALAPPDATA% directly (see
// AppStartupConfig.ApplyFpsConsentFromInstallState). If it recorded fpsConsent=true in
// install-state.json instead, apply it to the per-user config now, unelevated, on first start.
var installStatePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HardwareLive", "install-state.json");
var perUserConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareLive", "config.json");
AppStartupConfig.ApplyFpsConsentFromInstallState(installStatePath, configPath, perUserConfigPath);

var parsedArgs = AppArguments.Parse(args);

// Reopen path (docs/SPEC.md Component 8 step 5): a second launch never starts a second
// server -- it would just fail the port bind -- it only (optionally) opens the window.
using var instanceGuard = new SingleInstanceGuard();
if (!instanceGuard.IsPrimaryInstance)
{
    if (WindowLaunchDecision.ShouldLaunchOnSecondaryInstance(parsedArgs.Open))
    {
        var secondaryPort = PortConfiguration.Resolve(args, configPath);
        DashboardWindowLauncher.Launch(DashboardUrl.Build(secondaryPort));
    }

    return;
}

var port = PortConfiguration.Resolve(args, configPath);
var thresholdConfig = UserThresholdConfig.Load(configPath);
var store = new TelemetryStore();
// Installed layout: <root>\app\hardware-live.exe and <root>\sampler\hl-sampler.exe.
// They must be separate folders: the sampler is published self-contained, and its private
// runtime (coreclr.dll, hostpolicy.dll, ...) in the app folder hangs the framework-dependent app.
var samplerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "sampler", "hl-sampler.exe"));
var samplerClient = new SamplerClient(store, samplerPath);
using var samplerCancellation = new CancellationTokenSource();
// Run on the thread pool so a synchronously-completing connect path can never block server startup.
var samplerTask = Task.Run(() => samplerClient.RunAsync(samplerCancellation.Token));

try
{
    await using var server = HardwareLiveServer.Create(port, store, thresholdConfig);
    await server.StartAsync();

    if (WindowLaunchDecision.ShouldLaunchOnPrimaryInstance(
            parsedArgs.NoWindow, parsedArgs.Open, AppStartupConfig.ReadOpenWindowOnStart(configPath)))
    {
        DashboardWindowLauncher.Launch(DashboardUrl.Build(port));
    }

    await server.WaitForShutdownAsync();
}
finally
{
    samplerCancellation.Cancel();
    await samplerTask;
}
