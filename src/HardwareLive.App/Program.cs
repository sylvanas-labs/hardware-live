using HardwareLive.Core;

var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var port = PortConfiguration.Resolve(args, configPath);
var store = new TelemetryStore();
// Installed layout: <root>\app\HardwareLive.App.exe and <root>\sampler\hl-sampler.exe.
// They must be separate folders: the sampler is published self-contained, and its private
// runtime (coreclr.dll, hostpolicy.dll, ...) in the app folder hangs the framework-dependent app.
var samplerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "sampler", "hl-sampler.exe"));
var samplerClient = new SamplerClient(store, samplerPath);
using var samplerCancellation = new CancellationTokenSource();
// Run on the thread pool so a synchronously-completing connect path can never block server startup.
var samplerTask = Task.Run(() => samplerClient.RunAsync(samplerCancellation.Token));

try
{
    await using var server = HardwareLiveServer.Create(port, store);
    await server.StartAsync();
    await server.WaitForShutdownAsync();
}
finally
{
    samplerCancellation.Cancel();
    await samplerTask;
}
