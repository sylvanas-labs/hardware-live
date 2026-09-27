using System.Security.Cryptography;

namespace HardwareLive.Core.Fps;

/// <summary>The pinned PresentMon v2.6.0-x64 release asset (docs/SPEC.md Component 9),
/// verified 2026-09-27 against the real GitHub asset digest.</summary>
public static class PresentMonHash
{
    public const string ExpectedSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const long ExpectedSizeBytes = 980320;

    public static bool Verify(string exePath)
    {
        if (!File.Exists(exePath))
        {
            return false;
        }

        using var stream = File.OpenRead(exePath);
        var hash = SHA256.HashData(stream);
        var hex = Convert.ToHexStringLower(hash);
        return string.Equals(hex, ExpectedSha256, StringComparison.Ordinal);
    }
}
