using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Protocol;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class SamplerClientIntegrationTests
{
    [AdminFact]
    public async Task AcceptedPipeFrameFlowsThroughStoreAndHttpApis()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = PipeNames.ForUser(user);
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        var store = new TelemetryStore();
        var client = new SamplerClient(store, "expected.exe", new FixedVerifier(true), user);
        using var cancellation = new CancellationTokenSource();
        var clientTask = client.RunAsync(cancellation.Token);

        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await FrameProtocol.WriteAsync(server, Frame());
            await WaitUntilAsync(() => store.LatestFrame is not null);
            await using var host = await ServerTestHost.StartAsync(store);

            using var snapshotResponse = await host.Client.GetAsync("/api/snapshot");
            using var snapshot = await JsonDocument.ParseAsync(await snapshotResponse.Content.ReadAsStreamAsync());
            using var metaResponse = await host.Client.GetAsync("/api/meta");
            using var meta = await JsonDocument.ParseAsync(await metaResponse.Content.ReadAsStreamAsync());
            using var healthResponse = await host.Client.GetAsync("/api/health");
            using var health = await JsonDocument.ParseAsync(await healthResponse.Content.ReadAsStreamAsync());

            Assert.Equal(77, snapshot.RootElement.GetProperty("sequence").GetInt64());
            Assert.Equal(123456, snapshot.RootElement.GetProperty("timestampUnixMs").GetInt64());
            Assert.False(snapshot.RootElement.GetProperty("stale").GetBoolean());
            Assert.Equal(62.5f, snapshot.RootElement.GetProperty("sensors")[0].GetProperty("value").GetSingle());
            Assert.Equal(62.5f, snapshot.RootElement.GetProperty("history").GetProperty("/cpu/0/temp/0")[0].GetSingle());
            Assert.Equal("CPU", meta.RootElement.GetProperty("hardware")[0].GetProperty("name").GetString());
            Assert.Equal("Package", meta.RootElement.GetProperty("sensors")[0].GetProperty("name").GetString());
            Assert.Equal("unmapped: cpu.temp.control", health.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            cancellation.Cancel();
            await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AdminFact]
    public async Task RejectedPipeStoresNoFramesAndReportsIdentityMismatch()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = PipeNames.ForUser(user);
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        var store = new TelemetryStore();
        var client = new SamplerClient(store, "expected.exe", new FixedVerifier(false), user);
        using var cancellation = new CancellationTokenSource();
        var clientTask = client.RunAsync(cancellation.Token);

        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => store.HasSamplerIdentityMismatch);
            await using var host = await ServerTestHost.StartAsync(store);

            using var response = await host.Client.GetAsync("/api/health");
            using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            Assert.Null(store.LatestFrame);
            Assert.Equal("UNKNOWN", health.RootElement.GetProperty("status").GetString());
            Assert.Equal("sampler identity mismatch", health.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            cancellation.Cancel();
            await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AdminFact]
    public async Task RejectsWhenOwnerCheckFailsEvenIfImagePathMatches()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = PipeNames.ForUser(user);
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        var store = new TelemetryStore();
        var client = new SamplerClient(store, "expected.exe", new SplitVerifier(owner: false, image: true), user);
        using var cancellation = new CancellationTokenSource();
        var clientTask = client.RunAsync(cancellation.Token);

        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => store.HasSamplerIdentityMismatch);

            Assert.Null(store.LatestFrame);
        }
        finally
        {
            cancellation.Cancel();
            await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AdminFact]
    public async Task InvalidJsonFrameIsDroppedButTheConnectionStaysUpForTheNextFrame()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = PipeNames.ForUser(user);
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        var store = new TelemetryStore();
        var client = new SamplerClient(store, "expected.exe", new FixedVerifier(true), user);
        using var cancellation = new CancellationTokenSource();
        var clientTask = client.RunAsync(cancellation.Token);

        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // Missing every required field: RespectRequiredConstructorParameters makes
            // this a JsonException, not a silently-defaulted frame.
            await WriteRawFrameAsync(server, "{\"version\":1}"u8.ToArray());
            await FrameProtocol.WriteAsync(server, Frame());
            await WaitUntilAsync(() => store.LatestFrame is not null);

            Assert.False(clientTask.IsCompleted);
            Assert.Equal(77, store.LatestFrame!.Sequence);
            Assert.True(client.DroppedFrameCount >= 1);
        }
        finally
        {
            cancellation.Cancel();
            await clientTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WriteRawFrameAsync(Stream stream, byte[] payload)
    {
        var prefix = new byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static SensorFrame Frame() =>
        new(
            SensorFrame.CurrentVersion,
            77,
            123456,
            PawnIoInstalled: true,
            Elevated: true,
            LhmVersion: "0.9.6.0",
            Hardware: [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            Sensors: [new SensorReading("/cpu/0/temp/0", "/cpu/0", "Package", "Temperature", 62.5f, 40f, 70f)]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected sampler state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FixedVerifier(bool result) : IProcessImageVerifier
    {
        public bool IsExpectedServer(NamedPipeClientStream pipe, string expectedImagePath) => result;

        public bool HasExpectedOwner(NamedPipeClientStream pipe) => result;
    }

    private sealed class SplitVerifier(bool owner, bool image) : IProcessImageVerifier
    {
        public bool IsExpectedServer(NamedPipeClientStream pipe, string expectedImagePath) => image;

        public bool HasExpectedOwner(NamedPipeClientStream pipe) => owner;
    }
}
