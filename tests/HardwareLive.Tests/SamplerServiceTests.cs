using System.Security.Principal;
using HardwareLive.Protocol;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class SamplerServiceTests
{
    [AdminFact]
    public async Task ExistingPerUserPipeReturnsExitCodeThree()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = PipeNames.ForUser(user);
        await using var existing = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        using var sampler = new FakeSampler();
        var messages = new List<string>();
        var service = new SamplerService(sampler, user, messages.Add);

        var exitCode = await service.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, exitCode);
        Assert.Contains(messages, message => message.Contains("already in use", StringComparison.Ordinal));
    }

    private sealed class FakeSampler : ISensorFrameSampler
    {
        public SensorFrame Sample() =>
            new(
                SensorFrame.CurrentVersion,
                1,
                1,
                false,
                false,
                "0.9.6.0",
                [],
                []);

        public void Dispose()
        {
        }
    }
}
