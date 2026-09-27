using System.Security.Principal;

namespace HardwareLive.Sampler;

public enum SamplerCommandKind
{
    Serve,
    Dump,
    Help,
    Invalid,
}

public sealed record SamplerCommand(
    SamplerCommandKind Kind,
    string? DumpPath = null,
    SecurityIdentifier? UserSid = null,
    bool DevAllowNonSystem = false)
{
    public static SamplerCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "--help", StringComparison.Ordinal))
        {
            return new SamplerCommand(SamplerCommandKind.Help);
        }

        if (args.Count == 2 &&
            string.Equals(args[0], "--dump", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(args[1]))
        {
            return new SamplerCommand(SamplerCommandKind.Dump, args[1]);
        }

        if (args.Count == 2 && string.Equals(args[0], "--user-sid", StringComparison.Ordinal))
        {
            var sid = TryParseTargetUserSid(args[1]);
            return sid is null
                ? new SamplerCommand(SamplerCommandKind.Invalid)
                : new SamplerCommand(SamplerCommandKind.Serve, UserSid: sid);
        }

#if DEBUG
        // Dev-only escape hatch (r5 hardening): lets a developer run serve mode from an
        // unelevated shell while iterating. Compiled out entirely in Release, so this
        // 3-arg shape is simply unrecognized there and falls through to Invalid below --
        // Release builds never parse `--dev-allow-non-system` at all.
        if (args.Count == 3 &&
            string.Equals(args[0], "--user-sid", StringComparison.Ordinal) &&
            string.Equals(args[2], "--dev-allow-non-system", StringComparison.Ordinal))
        {
            var sid = TryParseTargetUserSid(args[1]);
            return sid is null
                ? new SamplerCommand(SamplerCommandKind.Invalid)
                : new SamplerCommand(SamplerCommandKind.Serve, UserSid: sid, DevAllowNonSystem: true);
        }
#endif

        return new SamplerCommand(SamplerCommandKind.Invalid);
    }

    /// <summary>
    /// Accepts only local or domain user/group SIDs (S-1-5-21-...). Rejects well-known
    /// service accounts (LocalSystem S-1-5-18, LocalService S-1-5-19, NetworkService
    /// S-1-5-20) and any other well-known/malformed SID, so the elevated sampler can
    /// never be pointed at itself or another service identity.
    /// </summary>
    private static SecurityIdentifier? TryParseTargetUserSid(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return sid.Value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ? sid : null;
    }
}
