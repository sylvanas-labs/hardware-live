using System.IO.Pipes;
using System.Security.Principal;
using HardwareLive.Core;
using HardwareLive.Protocol;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class TimeoutTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task SamplerWriteTimesOutWhenTheClientNeverReads()
    {
        // A stream whose WriteAsync never completes models a connected client that never
        // drains its receive buffer. This is deterministic and fast, unlike waiting for a
        // real named pipe's OS buffer to fill.
        await using var stream = new NeverCompletingWriteStream();
        var frame = ProtocolFramingTests.CreateFrame();

        var wrote = await SamplerService.TryWriteFrameAsync(stream, frame, ShortTimeout, CancellationToken.None);

        Assert.False(wrote);
    }

    [Fact]
    public async Task SamplerWriteSucceedsWithinTheTimeout()
    {
        await using var stream = new MemoryStream();
        var frame = ProtocolFramingTests.CreateFrame();

        var wrote = await SamplerService.TryWriteFrameAsync(stream, frame, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(wrote);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task SamplerWriteTimeoutHonorsExternalCancellation()
    {
        await using var stream = new NeverCompletingWriteStream();
        var frame = ProtocolFramingTests.CreateFrame();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SamplerService.TryWriteFrameAsync(stream, frame, TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public async Task ClientReadTimesOutWhenTheServerNeverWrites()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.ReadTimeout.{Guid.NewGuid():N}";
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.None,
            HandleInheritability.None);
        var waitForClient = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waitForClient;

        await Assert.ThrowsAsync<TimeoutException>(
            () => SamplerClient.TryReadFrameAsync(client, ShortTimeout, CancellationToken.None));
    }

    [Fact]
    public async Task ClientReadSucceedsWhenAFrameArrivesBeforeTheTimeout()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.ReadOk.{Guid.NewGuid():N}";
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.None,
            HandleInheritability.None);
        var waitForClient = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waitForClient.WaitAsync(TimeSpan.FromSeconds(5));

        // Start the read before/concurrently with the write: a byte-mode named pipe with
        // no reader actively pending can leave a small write stuck, exactly like a real
        // sampler frame push racing the client's read loop.
        var readTask = SamplerClient.TryReadFrameAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await FrameProtocol.WriteAsync(server, ProtocolFramingTests.CreateFrame()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var frame = await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(frame);
    }

    private sealed class NeverCompletingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
