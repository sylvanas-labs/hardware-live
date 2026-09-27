namespace HardwareLive.App;

/// <summary>
/// The dashboard window always targets the IPv4 loopback literal, never <c>localhost</c>:
/// the server binds <c>127.0.0.1</c> explicitly (docs/SPEC.md Invariant 2), and on some
/// machines <c>localhost</c> resolves to <c>::1</c> first, which nothing is listening on.
/// </summary>
public static class DashboardUrl
{
    public static string Build(int port) => $"http://127.0.0.1:{port}/";
}
