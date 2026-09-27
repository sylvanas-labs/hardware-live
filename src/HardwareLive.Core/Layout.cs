namespace HardwareLive.Core;

public sealed record Layout(
    string Id,
    string Name,
    IReadOnlyList<LayoutWidget> Widgets,
    string? Focus = null,
    string? Sort = null);

/// <summary>One widget in a layout. Non-chart kinds always carry <see cref="Ref"/> and never
/// <see cref="Series"/>/<see cref="Max"/>. A chart carries exactly one of <see cref="Ref"/>
/// (single series, backward compatible with step 1's schema) or <see cref="Series"/> (up to
/// 12 series), plus an optional fixed <see cref="Max"/> for its Y axis (e.g. 100 for a load
/// chart). See <see cref="LayoutJson"/> for the exact validation rules.</summary>
public sealed record LayoutWidget(
    string Kind,
    string Size,
    LayoutReference? Ref,
    IReadOnlyList<LayoutReference>? Series = null,
    double? Max = null);

public sealed record LayoutReference(string? Role, string? Id, string? Hw);
