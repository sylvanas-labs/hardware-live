using System.Security.Principal;
using HardwareLive.Protocol;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

/// <summary>
/// Finding 1 (r5 hardening): serve mode must refuse to run unless the process token is
/// SYSTEM. <c>isSystemIdentity</c> is the injectable seam SamplerApplication.RunAsync
/// exposes so these tests never need a real elevated/SYSTEM token.
/// </summary>
public sealed class SamplerApplicationTests
{
    private const string ValidUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public async Task ServeModeWithoutSystemIdentityIsRejectedWithExitCodeFour()
    {
        var granted = false;
        var exitCode = await SamplerApplication.RunAsync(
            ["--user-sid", ValidUserSid],
            isSystemIdentity: () => false,
            createSampler: () => new FakeSampler(),
            grantProcessQuery: _ => granted = true);

        Assert.Equal(4, exitCode);
        Assert.False(granted);
    }

    [Fact]
    public async Task ServeModeWithSystemIdentityProceedsPastTheGate()
    {
        // A pre-cancelled token makes SamplerService.RunAsync skip its pipe-serving loop
        // entirely and return 0 (see SamplerService.RunAsync: the while-loop condition is
        // false from the start), so this never touches the real per-user/SYSTEM-owned
        // named pipe and needs no elevation -- it only proves the gate itself let a
        // SYSTEM (faked) caller through to SamplerService, rather than returning 4.
        using var identity = WindowsIdentity.GetCurrent();
        var currentUserSid = identity.User!.Value;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        SecurityIdentifier? grantedTo = null;
        var exitCode = await SamplerApplication.RunAsync(
            ["--user-sid", currentUserSid],
            cts.Token,
            isSystemIdentity: () => true,
            createSampler: () => new FakeSampler(),
            grantProcessQuery: sid => grantedTo = sid);

        Assert.Equal(0, exitCode);
        // The unelevated app can only verify the SYSTEM sampler's image path if serve mode
        // grants the target user PROCESS_QUERY_LIMITED_INFORMATION on the sampler process.
        Assert.Equal(new SecurityIdentifier(currentUserSid), grantedTo);
    }

    [Fact]
    public void GrantQueryLimitedInformationAppendsOneNarrowAceAndKeepsTheExistingDacl()
    {
        // Real round-trip on this test process's own DACL (harmless: it dies with the
        // process). A SID nobody holds, so the ACE is distinguishable from existing ones.
        var testSid = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-4242");
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var systemBefore = SamplerProcessSecurity.GetAllowMasksFor(system);
        var userBefore = SamplerProcessSecurity.GetAllowMasksFor(currentUser);
        Assert.Empty(SamplerProcessSecurity.GetAllowMasksFor(testSid));

        SamplerProcessSecurity.GrantQueryLimitedInformation(testSid);

        Assert.Equal([SamplerProcessSecurity.ProcessQueryLimitedInformation], SamplerProcessSecurity.GetAllowMasksFor(testSid));
        Assert.Equal(systemBefore, SamplerProcessSecurity.GetAllowMasksFor(system));
        Assert.Equal(userBefore, SamplerProcessSecurity.GetAllowMasksFor(currentUser));
    }

    [Fact]
    public async Task DumpModeNeverConsultsTheSystemIdentityGate()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), $"hl-dump-test-{Guid.NewGuid():N}.json");
        try
        {
            var gateCalled = false;
            var exitCode = await SamplerApplication.RunAsync(
                ["--dump", dumpPath],
                isSystemIdentity: () =>
                {
                    gateCalled = true;
                    return false;
                },
                createSampler: () => new FakeSampler());

            Assert.Equal(0, exitCode);
            Assert.False(gateCalled);
            Assert.True(File.Exists(dumpPath));
        }
        finally
        {
            File.Delete(dumpPath);
        }
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
