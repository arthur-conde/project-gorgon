using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tier 1 state handler for player position. Registered for both
/// <see cref="Verbs.ProcessNewPosition"/> and <see cref="Verbs.ProcessAddPlayer"/>.
/// Differentiates by args layout: ProcessNewPosition starts with <c>((</c> (nested
/// position tuple first), ProcessAddPlayer starts with <c>(digit</c> (entity id first,
/// position follows <c>System.String[]</c>).
/// </summary>
internal sealed class Position(IDomainEventPublisher bus) : IFrameHandler, IPositionState
{
    public double? X { get; private set; }
    public double? Y { get; private set; }
    public double? Z { get; private set; }
    public DateTimeOffset? MeasuredAt { get; private set; }
    public PositionSource? Source { get; private set; }

    internal void Reset()
    {
        X = null;
        Y = null;
        Z = null;
        MeasuredAt = null;
        Source = null;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (args.Length >= 2 && args[0] == '(' && args[1] == '(')
            HandleNewPosition(args, metadata);
        else
            HandleSpawnPosition(args, metadata);
    }

    private void HandleNewPosition(ReadOnlySpan<char> args, LogLineMetadata metadata)
    {
        if (!TryExtractFirstNestedTuple(args, out var x, out var y, out var z))
            return;

        UpdateAndPublish(x, y, z, PositionSource.Movement, metadata);
    }

    private void HandleSpawnPosition(ReadOnlySpan<char> args, LogLineMetadata metadata)
    {
        const string marker = "System.String[]";
        var markerIdx = args.IndexOf(marker.AsSpan(), StringComparison.Ordinal);
        if (markerIdx < 0) return;

        var rest = args[(markerIdx + marker.Length)..];
        var parenOpen = rest.IndexOf('(');
        if (parenOpen < 0) return;
        var tupleStart = rest[(parenOpen + 1)..];
        var parenClose = tupleStart.IndexOf(')');
        if (parenClose < 0) return;

        if (!TryParseCoords(tupleStart[..parenClose], out var x, out var y, out var z))
            return;

        UpdateAndPublish(x, y, z, PositionSource.Spawn, metadata);
    }

    private void UpdateAndPublish(double x, double y, double z, PositionSource source, LogLineMetadata metadata)
    {
        X = x;
        Y = y;
        Z = z;
        MeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        Source = source;
        bus.Publish(new PlayerPositionChanged(x, y, z, source, metadata));
    }

    /// <summary>
    /// Extract coordinates from the first nested <c>((x, y, z), ...)</c> tuple in an args span.
    /// </summary>
    private static bool TryExtractFirstNestedTuple(
        ReadOnlySpan<char> args, out double x, out double y, out double z)
    {
        x = y = z = 0;
        var firstParen = args.IndexOf('(');
        if (firstParen < 0) return false;
        var afterFirst = args[(firstParen + 1)..];
        var secondParen = afterFirst.IndexOf('(');
        if (secondParen < 0) return false;
        var tupleStart = afterFirst[(secondParen + 1)..];
        var tupleEnd = tupleStart.IndexOf(')');
        if (tupleEnd < 0) return false;

        return TryParseCoords(tupleStart[..tupleEnd], out x, out y, out z);
    }

    private static bool TryParseCoords(ReadOnlySpan<char> span, out double x, out double y, out double z)
    {
        x = y = z = 0;
        var sep1 = span.IndexOf(',');
        if (sep1 < 0) return false;
        if (!double.TryParse(span[..sep1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x))
            return false;

        var rest = span[(sep1 + 1)..];
        var sep2 = rest.IndexOf(',');
        if (sep2 < 0) return false;
        if (!double.TryParse(rest[..sep2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            return false;

        if (!double.TryParse(rest[(sep2 + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            return false;

        return true;
    }
}
