using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's current area via the two-phase load sequence:
/// <c>LOADING_LEVEL AreaKey</c> (pending) → <c>InitializingArea AreaKey</c> (confirmed).
/// Non-area keys and bare LOADING_LEVEL clear state immediately.
/// <para>
/// Registered for both <see cref="Verbs.LoadingLevel"/> and <see cref="Verbs.InitializingArea"/>.
/// Differentiates by args format: InitializingArea args start with '(' (the instance id).
/// </para>
/// <para>
/// <c>_pendingArea</c> is a loading-in-progress indicator only; <c>InitializingArea</c>
/// carries the authoritative area key. The two-phase model is "LOADING_LEVEL suppresses /
/// signals load; InitializingArea resolves."
/// </para>
/// </summary>
internal sealed class Map : IFrameHandler, IAreaState
{
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _areaPool;
    private string? _pendingArea;

    public string? CurrentArea { get; private set; }

    internal string? PendingArea => _pendingArea;

    public Map(IDomainEventPublisher bus, InternPool areaPool)
    {
        _bus = bus;
        _areaPool = areaPool;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // InitializingArea args start with '(' (instance id prefix).
        // LOADING_LEVEL args are either empty or a bare area key.
        if (args.Length > 0 && args[0] == '(')
            HandleInitializingArea(args, metadata);
        else
            HandleLoadingLevel(args, metadata);
    }

    private void HandleLoadingLevel(ReadOnlySpan<char> args, LogLineMetadata metadata)
    {
        if (args.IsEmpty || IsNonAreaKey(args))
        {
            _pendingArea = null;
            if (CurrentArea is not null)
                SetArea(null, metadata);
            return;
        }

        _pendingArea = _areaPool.InternOrAllocate(args);
    }

    private void HandleInitializingArea(ReadOnlySpan<char> args, LogLineMetadata metadata)
    {
        // Format: "(502934): AreaSerbule" — extract the area key after ": "
        var colonSpace = args.IndexOf(": ");
        if (colonSpace < 0)
            return;

        var areaSpan = args[(colonSpace + 2)..];
        if (areaSpan.IsEmpty)
            return;

        var area = _areaPool.InternOrAllocate(areaSpan);
        _pendingArea = null;
        SetArea(area, metadata);
    }

    private void SetArea(string? newArea, LogLineMetadata metadata)
    {
        var previous = CurrentArea;
        if (string.Equals(previous, newArea, StringComparison.Ordinal))
            return;

        CurrentArea = newArea;
        _bus.Publish(new AreaChanged(previous, newArea, metadata));
    }

    private static bool IsNonAreaKey(ReadOnlySpan<char> args) => WellKnownArgs.IsNonAreaKey(args);
}
