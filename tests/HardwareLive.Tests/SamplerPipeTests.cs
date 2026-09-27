using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class SamplerPipeTests
{
    [Fact]
    public void PipeSecurityHasOnlyTheThreeExplicitProtectedRules()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = SamplerPipeSecurity.Create(user);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(3, rules.Length);
        AssertRule(
            rules,
            user,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize);
        AssertRule(rules, localSystem, PipeAccessRights.FullControl);
        AssertRule(rules, administrators, PipeAccessRights.FullControl);

        var userRule = Assert.Single(rules, rule => user.Equals(rule.IdentityReference));
        Assert.Equal(
            (PipeAccessRights)0,
            userRule.PipeAccessRights & (PipeAccessRights.WriteData | PipeAccessRights.CreateNewInstance));
    }

    [Fact]
    public void PipeOwnerIsSetToBuiltinAdministrators()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = SamplerPipeSecurity.Create(user);

        Assert.Equal(administrators, security.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public async Task ProductionFactoryCreatesOutboundOnlyPipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.Direction.{Guid.NewGuid():N}";
        await using var server = SamplerPipeFactory.Create(pipeName, SamplerPipeSecurity.Create(user));
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.None,
            HandleInheritability.None);

        var waitForClient = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waitForClient;

        Assert.False(server.CanRead);
        Assert.True(server.CanWrite);
        Assert.True(client.CanRead);
        Assert.False(client.CanWrite);
    }

    [Fact]
    public void FirstPipeInstancePreventsASecondServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = SamplerPipeSecurity.Create(user);
        var pipeName = $"HardwareLive.Tests.First.{Guid.NewGuid():N}";
        using var first = SamplerPipeFactory.Create(pipeName, security);

        var exception = Record.Exception(() => SamplerPipeFactory.Create(pipeName, security));

        Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());
    }

    private static void AssertRule(
        IEnumerable<PipeAccessRule> rules,
        SecurityIdentifier sid,
        PipeAccessRights rights)
    {
        var rule = Assert.Single(rules, candidate => sid.Equals(candidate.IdentityReference));
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
        Assert.Equal(rights, rule.PipeAccessRights);
    }
}
