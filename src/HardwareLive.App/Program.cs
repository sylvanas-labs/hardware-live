using HardwareLive.Core;
using HardwareLive.Core.Fps;
using HardwareLive.Core.Profiles;

// %LOCALAPPDATA%\HardwareLive\config.json takes priority (the app runs unelevated and
// Program Files is read-only); the app-base-directory file is a backward-compatible
// fallback for installs that predate this location.
var configPath = ConfigPaths.Resolve(AppContext.BaseDirectory);
var port = PortConfiguration.Resolve(args, configPath);
var thresholdConfig = UserThresholdConfig.Load(configPath);
var store = new TelemetryStore();
// Installed layout: <root>\app\HardwareLive.App.exe and <root>\sampler\hl-sampler.exe.
// They must be separate folders: the sampler is published self-contained, and its private
// runtime (coreclr.dll, hostpolicy.dll, ...) in the app folder hangs the framework-dependent app.
var samplerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "sampler", "hl-sampler.exe"));
var samplerClient = new SamplerClient(store, samplerPath);
using var samplerCancellation = new CancellationTokenSource();
// Run on the thread pool so a synchronously-completing connect path can never block server startup.
var samplerTask = Task.Run(() => samplerClient.RunAsync(samplerCancellation.Token));

// PresentMon lives beside the app under a "presentmon" subfolder (docs/SPEC.md step7-fps
// item "App csproj"); FPS stays opt-in (fps.enabled in config.json, default false) even when
// the exe is present.
var presentMonPath = Path.Combine(AppContext.BaseDirectory, "presentmon", "PresentMon-2.6.0-x64.exe");
var fpsService = new FpsService(configPath, presentMonPath);
store.FrameAugmentor = fpsService;
using var fpsCancellation = new CancellationTokenSource();
var fpsTask = Task.Run(() => fpsService.RunAsync(fpsCancellation.Token));

try
{
    await using var server = HardwareLiveServer.Create(new HardwareLiveServerOptions
    {
        Port = port,
        Telemetry = store,
        ThresholdConfig = thresholdConfig,
        Fps = fpsService,
    });
    await server.StartAsync();
    await server.WaitForShutdownAsync();
}
finally
{
    samplerCancellation.Cancel();
    fpsCancellation.Cancel();
    await samplerTask;
    await fpsTask;
}
