using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class SamplerCommandTests
{
    private const string ValidUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void NoArgumentsIsInvalid()
    {
        // Serve mode now requires an explicit --user-sid (r5): the sampler runs as
        // SYSTEM and must never infer the target user from its own (SYSTEM) token.
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse([]).Kind);
    }

    [Fact]
    public void UserSidSelectsServeWithTheParsedSid()
    {
        var command = SamplerCommand.Parse(["--user-sid", ValidUserSid]);

        Assert.Equal(SamplerCommandKind.Serve, command.Kind);
        Assert.NotNull(command.UserSid);
        Assert.Equal(ValidUserSid, command.UserSid!.Value);
    }

    [Fact]
    public void EntraIdUserSidIsAccepted()
    {
        // Microsoft Entra ID (Azure AD) accounts, common on work PCs, have S-1-12-1-... SIDs.
        const string entraSid = "S-1-12-1-1234567890-1234567890-1234567890-1234567890";
        var command = SamplerCommand.Parse(["--user-sid", entraSid]);

        Assert.Equal(SamplerCommandKind.Serve, command.Kind);
        Assert.Equal(entraSid, command.UserSid!.Value);
    }

    [Theory]
    [InlineData("S-1-12-2-1-2-3-4")] // other S-1-12 forms are not user accounts
    [InlineData("S-1-5-18")] // LocalSystem
    [InlineData("S-1-5-19")] // LocalService
    [InlineData("S-1-5-20")] // NetworkService
    [InlineData("S-1-5-32-544")] // BUILTIN\Administrators (well-known alias, not S-1-5-21)
    [InlineData("not-a-sid")]
    [InlineData(" ")]
    public void ServiceAndMalformedSidsAreRejected(string sid)
    {
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--user-sid", sid]).Kind);
    }

    [Fact]
    public void UserSidRequiresExactlyOneValue()
    {
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--user-sid"]).Kind);
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--user-sid", ValidUserSid, "extra"]).Kind);
    }

    [Fact]
    public void DumpRequiresExactlyOneNonEmptyPathAndNoSid()
    {
        var command = SamplerCommand.Parse(["--dump", "fixture.json"]);

        Assert.Equal(SamplerCommandKind.Dump, command.Kind);
        Assert.Equal("fixture.json", command.DumpPath);
        Assert.Null(command.UserSid);
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--dump"]).Kind);
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--dump", " "]).Kind);
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--dump", "a", "b"]).Kind);
    }

    [Fact]
    public void HelpAndUnknownArgumentsAreDistinct()
    {
        Assert.Equal(SamplerCommandKind.Help, SamplerCommand.Parse(["--help"]).Kind);
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(["--wat"]).Kind);
    }

    [Fact]
    public void DevAllowNonSystemFlagOnlyParsesInADebugBuild()
    {
        // r5 hardening: the flag is compiled under #if DEBUG in SamplerCommand.Parse, so
        // its recognition depends on the build configuration the *test assembly* runs
        // under -- the baseline gate for this repo is `dotnet test -c Release`, which
        // never defines DEBUG, so this test exercises (and pins) the Release behavior:
        // the flag is unrecognized and the whole command is Invalid.
        var args = new[] { "--user-sid", ValidUserSid, "--dev-allow-non-system" };
#if DEBUG
        var command = SamplerCommand.Parse(args);
        Assert.Equal(SamplerCommandKind.Serve, command.Kind);
        Assert.True(command.DevAllowNonSystem);
#else
        Assert.Equal(SamplerCommandKind.Invalid, SamplerCommand.Parse(args).Kind);
#endif
    }
}
