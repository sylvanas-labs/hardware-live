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

    [Theory]
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
}
