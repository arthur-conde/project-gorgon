using System.Globalization;
using System.Text.RegularExpressions;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// <see cref="ILineObserver"/> that matches <c>Download appearance loop @Model(scale=N)</c>
/// lines and publishes <see cref="AppearanceLoopFrame"/>. These lines are not verb-dispatched
/// (no <c>Process*</c> prefix), so the observer pattern (like Calendar) is the correct hook.
/// Primary consumer: Samwise (plant model detection).
/// </summary>
internal sealed partial class AppearanceObserver(IDomainEventPublisher bus) : ILineObserver
{
    [GeneratedRegex(
        @"Download appearance loop @(\w+)\(scale=([\d.]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AppearanceRx();

    public void Observe(string log, LogLineMetadata metadata)
    {
        if (!log.Contains("appearance loop", StringComparison.Ordinal))
            return;

        var match = AppearanceRx().Match(log);
        if (!match.Success)
            return;

        var modelGroup = match.Groups[1];
        var modelMem = log.AsMemory(modelGroup.Index, modelGroup.Length);

        if (!double.TryParse(match.Groups[2].ValueSpan, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var scale))
            return;

        bus.Publish(new AppearanceLoopFrame(modelMem, scale, metadata));
    }
}
