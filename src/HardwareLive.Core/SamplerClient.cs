using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HardwareLive.Protocol;

namespace HardwareLive.Core;

public sealed class SamplerClient
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TelemetryStore _store;
    private readonly string _expectedSamplerPath;
    private readonly IProcessImageVerifier _verifier;
    private readonly SecurityIdentifier _user;
    private readonly TimeSpan _readTimeout;
    private readonly Action<string> _log;
    private int _droppedFrameCount;

    public SamplerClient(
        TelemetryStore store,
        string? expectedSamplerPath = null,
        IProcessImageVerifier? verifier = null,
        SecurityIdentifier? user = null,
        Action<string>? log = null,
        TimeSpan? readTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _expectedSamplerPath = Path.GetFullPath(expectedSamplerPath ?? Path.Combine(AppContext.BaseDirectory, "hl-sampler.exe"));
        _verifier = verifier ?? new ProcessImageVerifier();
        _user = user ?? GetCurrentUser();
        _log = log ?? (_ => { });
        _readTimeout = readTimeout ?? DefaultReadTimeout;
    }

    public string ExpectedSamplerPath => _expectedSamplerPath;

    /// <summary>Frames dropped after a successful connection: invalid JSON, or a frame
    /// that failed <see cref="FrameValidator"/>. The connection stays up; only the one
    /// bad frame is discarded.</summary>
    public int DroppedFrameCount => Volatile.Read(ref _droppedFrameCount);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var backoff = InitialBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateClient(PipeNames.ForUser(_user));
                await pipe.ConnectAsync(1000, cancellationToken);

                if (!_verifier.HasExpectedOwner(pipe) || !_verifier.IsExpectedServer(pipe, _expectedSamplerPath))
                {
                    _store.SetSamplerIdentityMismatch(true);
                    _log("sampler identity mismatch");
                }
                else
                {
                    _store.SetSamplerIdentityMismatch(false);
                    backoff = InitialBackoff;
                    await ReadFramesUntilDisconnectOrTimeoutAsync(pipe, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                _log($"Sampler connection failed: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never let an unanticipated failure end the client permanently: log,
                // back off, and retry the connection.
                _log($"Sampler client failed unexpectedly: {exception}");
            }

            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(MaximumBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }

    private async Task ReadFramesUntilDisconnectOrTimeoutAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SensorFrame? frame;
            try
            {
                frame = await TryReadFrameAsync(pipe, _readTimeout, cancellationToken);
            }
            catch (InvalidFrameException exception) when (exception.InnerException is System.Text.Json.JsonException)
            {
                // The length prefix was read correctly (framing is intact); only the
                // payload's JSON was malformed. Drop this one frame and keep reading on
                // the same connection instead of disconnecting.
                Interlocked.Increment(ref _droppedFrameCount);
                _log($"Dropped an invalid sampler frame: {exception.Message}");
                continue;
            }

            if (frame is null)
            {
                return;
            }

            if (!FrameValidator.Validate(frame))
            {
                Interlocked.Increment(ref _droppedFrameCount);
                _log($"Dropped a frame that failed validation (sequence {frame.Sequence}).");
                continue;
            }

            _store.Add(frame);
        }
    }

    /// <summary>
    /// Reads one frame, disconnecting (via <see cref="TimeoutException"/>) if none
    /// completes within <paramref name="timeout"/>. Public so tests can exercise the
    /// timeout behavior directly against a real, silent pipe server.
    /// </summary>
    public static async Task<SensorFrame?> TryReadFrameAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await FrameProtocol.ReadAsync(stream, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No sampler frame arrived within {timeout}.");
        }
    }

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(
            ".",
            pipeName,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.None,
            HandleInheritability.None);

    private static SecurityIdentifier GetCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows token has no user SID.");
    }
}
