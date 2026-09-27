using HardwareLive.Core;

var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var port = PortConfiguration.Resolve(args, configPath);

await using var server = HardwareLiveServer.Create(port);
await server.StartAsync();
await server.WaitForShutdownAsync();
