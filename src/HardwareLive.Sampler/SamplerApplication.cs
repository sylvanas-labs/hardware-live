using System.Security.Principal;
using System.Text.Json;
using HardwareLive.Protocol;

namespace HardwareLive.Sampler;

public static class SamplerApplication
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default,
        Func<bool>? isSystemIdentity = null,
        Func<ISensorFrameSampler>? createSampler = null)
    {
        isSystemIdentity ??= DefaultIsSystemIdentity;
        createSampler ??= static () => new HardwareSensorSampler();

        var command = SamplerCommand.Parse(args);
        if (command.Kind == SamplerCommandKind.Invalid)
        {
            SamplerLog.Write("Invalid arguments. Use --help for usage.");
            return 2;
        }

        if (command.Kind == SamplerCommandKind.Help)
        {
            Console.WriteLine("Usage: hl-sampler.exe --user-sid <SID> | --dump <path.json> | --help");
            return 0;
        }

        // Serve mode is the only mode that opens the SYSTEM-owned pipe and streams live
        // hardware telemetry; --dump stays available to anyone (docs/SPEC.md fixture
        // tool, component 10). command.DevAllowNonSystem can only ever be true in a
        // Debug build (see SamplerCommand.Parse's #if DEBUG branch), so Release always
        // enforces this unconditionally.
        if (command.Kind == SamplerCommandKind.Serve && !command.DevAllowNonSystem && !isSystemIdentity())
        {
            SamplerLog.Write("Serve mode requires the sampler process to run as SYSTEM. Refusing to start.");
            return 4;
        }

        try
        {
            using var sampler = createSampler();
            if (command.Kind == SamplerCommandKind.Dump)
            {
                sampler.Sample();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var frame = sampler.Sample();
                var json = JsonSerializer.Serialize(frame, SensorFrameJson.CreateIndentedOptions());
                await File.WriteAllTextAsync(command.DumpPath!, json, cancellationToken);
                return 0;
            }

            var user = command.UserSid ?? throw new InvalidOperationException("Serve mode requires a validated --user-sid.");
            var service = new SamplerService(sampler, user);
            return await service.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            SamplerLog.Write($"Sampler failed: {exception}");
            return 1;
        }
    }

    private static bool DefaultIsSystemIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch
        {
            return false;
        }
    }
}
