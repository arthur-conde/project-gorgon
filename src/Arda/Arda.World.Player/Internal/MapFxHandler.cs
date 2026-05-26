using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 2 passthrough handler for <c>ProcessMapFx</c>.
/// Args: <c>((x, y, z), int, int, "shortName", Category, "message")</c>.
/// Primary consumer: Legolas (survey pin placement + relative offset parsing).
/// </summary>
internal sealed class MapFxHandler(IDomainEventPublisher bus) : IFrameHandler
{
    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (!TryExtractPosition(args, out var x, out var y, out var z, out var afterTuple))
            return;

        var tok = new ArgTokenizer(afterTuple);
        tok.Skip(2); // two unknown ints between position tuple and short name
        var shortSpan = tok.NextQuotedSpan();
        var catSpan = tok.NextTokenSpan();
        var msgSpan = tok.NextQuotedSpan();

        bus.Publish(new MapFxObserved(
            x, y, z,
            SpanHelpers.SliceFromSource(sourceLog, shortSpan),
            SpanHelpers.SliceFromSource(sourceLog, catSpan),
            SpanHelpers.SliceFromSource(sourceLog, msgSpan),
            metadata));
    }

    private static bool TryExtractPosition(
        ReadOnlySpan<char> args, out double x, out double y, out double z,
        out ReadOnlySpan<char> afterTuple)
    {
        x = y = z = 0;
        afterTuple = default;

        var outerOpen = args.IndexOf('(');
        if (outerOpen < 0) return false;
        var afterOuter = args[(outerOpen + 1)..];
        var innerOpen = afterOuter.IndexOf('(');
        if (innerOpen < 0) return false;
        var tupleStart = afterOuter[(innerOpen + 1)..];
        var tupleEnd = tupleStart.IndexOf(')');
        if (tupleEnd < 0) return false;

        var coordSpan = tupleStart[..tupleEnd];
        var sep1 = coordSpan.IndexOf(',');
        if (sep1 < 0) return false;
        if (!double.TryParse(coordSpan[..sep1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x))
            return false;
        var rest = coordSpan[(sep1 + 1)..];
        var sep2 = rest.IndexOf(',');
        if (sep2 < 0) return false;
        if (!double.TryParse(rest[..sep2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            return false;
        if (!double.TryParse(rest[(sep2 + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            return false;

        // Advance past the inner close ')' and skip the comma delimiter
        var remaining = tupleStart[(tupleEnd + 1)..];
        remaining = remaining.TrimStart();
        if (remaining.Length > 0 && remaining[0] == ',')
            remaining = remaining[1..].TrimStart();

        afterTuple = remaining;
        return true;
    }
}
