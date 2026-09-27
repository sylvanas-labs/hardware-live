namespace HardwareLive.Core;

public sealed record Layout(string Id, string Name, IReadOnlyList<LayoutWidget> Widgets);

public sealed record LayoutWidget(string Kind, string Size, LayoutReference Ref);

public sealed record LayoutReference(string? Role, string? Id, string? Hw);
