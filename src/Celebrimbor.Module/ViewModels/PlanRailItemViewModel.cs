namespace Celebrimbor.ViewModels;

/// <summary>Phase position in the walker rail relative to the walk cursor.</summary>
public enum PhaseRailState
{
    Done,
    Current,
    Upcoming,
}

/// <summary>
/// One item in the walker's phase rail (#228 PR-B/B1, frame 03). The rail
/// interleaves phases with the skill-source unlock callouts that fall between
/// them; <see cref="IsUnlock"/> discriminates. Unlock callouts that the live
/// character already knows are flagged (<see cref="AlreadyKnown"/>) for the
/// stale-walker view (frame 07).
/// </summary>
public sealed class PlanRailItemViewModel
{
    private PlanRailItemViewModel() { }

    public bool IsUnlock { get; private init; }

    // ── Phase ────────────────────────────────────────────────────────────
    public PhaseRailState State { get; private init; }
    public string RecipeName { get; private init; } = "";
    public string LevelRange { get; private init; } = "";
    public string Meta { get; private init; } = "";
    public bool UsesFirstTimeBonus { get; private init; }
    public bool ShowMiniProgress { get; private init; }
    public int MiniOnHand { get; private init; }
    public int MiniThreshold { get; private init; }
    public double MiniFraction { get; private init; }

    // ── Unlock callout ───────────────────────────────────────────────────
    public int UnlockAtLevel { get; private init; }
    public int UnlockXp { get; private init; }
    public string UnlockReason { get; private init; } = "";
    public bool AlreadyKnown { get; private init; }

    public static PlanRailItemViewModel Phase(
        PhaseRailState state, string recipeName, int levelStart, int levelEnd,
        int predictedCrafts, int xpPerCraft, bool usesFirstTimeBonus,
        bool showMiniProgress = false, int onHand = 0, int threshold = 0)
        => new()
        {
            IsUnlock = false,
            State = state,
            RecipeName = recipeName,
            LevelRange = $"L{levelStart} → L{levelEnd}",
            Meta = $"×{predictedCrafts} · {xpPerCraft} xp",
            UsesFirstTimeBonus = usesFirstTimeBonus,
            ShowMiniProgress = showMiniProgress,
            MiniOnHand = onHand,
            MiniThreshold = threshold,
            MiniFraction = threshold <= 0 ? 0d : Math.Clamp((double)onHand / threshold, 0d, 1d),
        };

    public static PlanRailItemViewModel Unlock(
        int atLevel, string recipeName, int xpAtUnlock, string reason, bool alreadyKnown)
        => new()
        {
            IsUnlock = true,
            UnlockAtLevel = atLevel,
            RecipeName = recipeName,
            UnlockXp = xpAtUnlock,
            UnlockReason = reason,
            AlreadyKnown = alreadyKnown,
        };
}
