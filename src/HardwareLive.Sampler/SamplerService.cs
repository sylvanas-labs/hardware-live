using System.IO.Pipes;
using System.Security.Principal;
using HardwareLive.Protocol;

namespace HardwareLive.Sampler;

public sealed class SamplerService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);

    private readonly ISensorFrameSampler _sampler;
    private readonly SecurityIdentifier _user;
    private readonly string _pipeName;
    private readonly Action<string> _log;
    private readonly TimeSpan _writeTimeout;
    private readonly object _frameLock = new();
    private SensorFrame? _latestFrame;

    public SamplerService(
        ISensorFrameSampler sampler,
        SecurityIdentifier user,
        Action<string>? log = null,
        TimeSpan? writeTimeout = null,
        string? pipeName = null)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        // Production always serves the real per-user sampler pipe; only a test supplies an
        // explicit override so it never collides with a real hl-sampler.exe on the same
        // machine (docs/SPEC.md: single-writer pipe, "All pipe instances are busy").
        _pipeName = pipeName ?? PipeNames.ForUser(_user);
        _log = log ?? SamplerLog.Write;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        SampleOnce();
        using var samplingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var samplingTask = SampleContinuouslyAsync(samplingCancellation.Token);

        try
        {
            var pipeName = _pipeName;
            var security = SamplerPipeSecurity.Create(_user);

            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = SamplerPipeFactory.Create(pipeName, security);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if ((exception.HResult & 0xFFFF) == 1307) // ERROR_INVALID_OWNER
                    {
                        _log($"Cannot set the pipe owner to Administrators; the sampler must run elevated or as SYSTEM: {exception.Message}");
                    }
                    else
                    {
                        _log($"Sampler pipe is already in use: {exception.Message}");
                    }

                    return 3;
                }

                await using (server)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken);
                        while (server.IsConnected && !cancellationToken.IsCancellationRequested)
                        {
                            var frame = GetLatestFrame();
                            if (frame is not null)
                            {
                                var wrote = await TryWriteFrameAsync(server, frame, _writeTimeout, cancellationToken);
                                if (!wrote)
                                {
                                    _log($"Sampler client did not accept a frame within {_writeTimeout}; dropping this client.");
                                    break;
                                }
                            }

                            await Task.Delay(Interval, cancellationToken);
                        }
                    }
                    catch (IOException exception)
                    {
                        _log($"Sampler client disconnected: {exception.Message}");
                    }
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            samplingCancellation.Cancel();
            try
            {
                await samplingTask;
            }
            catch (OperationCanceledException) when (samplingCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SampleContinuouslyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            SampleOnce();
        }
    }

    private void SampleOnce()
    {
        try
        {
            var frame = _sampler.Sample();
            lock (_frameLock)
            {
                _latestFrame = frame;
            }
        }
        catch (Exception exception)
        {
            _log($"Hardware sampling failed: {exception}");
        }
    }

    private SensorFrame? GetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestFrame;
        }
    }

    /// <summary>
    /// Writes one frame, giving up after <paramref name="timeout"/> if the peer never
    /// drains its receive buffer. Uses <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>
    /// rather than only cancelling the write's own token, so a peer/stream that doesn't
    /// observe cancellation still can't wedge the sampler forever. Public so tests can
    /// exercise the timeout directly against a stream double that never completes.
    /// </summary>
    public static async Task<bool> TryWriteFrameAsync(
        Stream stream,
        SensorFrame frame,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await FrameProtocol.WriteAsync(stream, frame, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
