namespace Arwen.Views;

/// <summary>
/// Static catalog of TTL choices offered in <c>ArwenSettingsView</c>.
/// Bound via <c>x:Static</c> so XAML doesn't need a backing VM property.
/// </summary>
public static class PendingTtlOptions
{
    public sealed record Option(string Label, TimeSpan Value);

    public static IReadOnlyList<Option> All { get; } =
    [
        new("1 hour", TimeSpan.FromHours(1)),
        new("6 hours", TimeSpan.FromHours(6)),
        new("24 hours", TimeSpan.FromHours(24)),
        new("3 days", TimeSpan.FromDays(3)),
        new("7 days", TimeSpan.FromDays(7)),
        new("30 days", TimeSpan.FromDays(30)),
    ];
}
