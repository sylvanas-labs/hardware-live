using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HardwareLive.Core;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class ProcessImageVerifierTests
{
    [Fact]
    public async Task VerifiesTheActualServerProcessImage()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.Identity.{Guid.NewGuid():N}";
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
        var verifier = new ProcessImageVerifier();

        Assert.True(verifier.IsExpectedServer(client, Environment.ProcessPath!));
        Assert.False(verifier.IsExpectedServer(client, Path.Combine(Path.GetTempPath(), "different.exe")));
    }

    [Fact]
    public async Task AcceptsAPipeOwnedByBuiltinAdministrators()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.Owner.Admin.{Guid.NewGuid():N}";
        // The real production ACL (SamplerPipeSecurity.Create) sets the owner to
        // BUILTIN\Administrators; this is the real "accept" path the shared elevated
        // test process can exercise (see SamplerPipeTests for the isolated ACL check).
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

        Assert.True(new ProcessImageVerifier().HasExpectedOwner(client));
    }

    [Fact]
    public async Task RejectsAPipeOwnedByTheCurrentUserInsteadOfAnAdminOrSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var pipeName = $"HardwareLive.Tests.Owner.User.{Guid.NewGuid():N}";
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // Explicit, not relying on the OS default owner: this test process is elevated,
        // and an unqualified default could resolve to Administrators too, silently
        // inverting the assertion.
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        await using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
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

        Assert.False(new ProcessImageVerifier().HasExpectedOwner(client));
    }
}
