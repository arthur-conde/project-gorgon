using System;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// An NPC favor tier, as a <strong>Neutral-centred signed rank</strong> — PG models
/// favor as a signed point scale around Neutral (you <em>lose</em> favor below it),
/// so the underlying values double as the ordinal rank: a higher value is strictly
/// better favor. This makes the query engine compare it correctly with no engine
/// changes (it already coerces enum literals by name and compares enums by
/// underlying value), so <c>CapIncreases WITH ANY (Tier &gt;= 'Friends')</c> works.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is floor-grounded (real favor-point thresholds): <c>Tolerated</c> sits
/// <em>below</em> <c>Neutral</c>. Spelling matches the reference-data token exactly
/// (<c>Hated</c>, not <c>Hatred</c>), so <see cref="ToToken"/> round-trips and a
/// member maps 1:1 to what <c>npcs.json</c> emits. Every real PG tier is a named
/// member — this is not a sentinel-bearing string; <see cref="Unknown"/> is the
/// <em>only</em> not-a-real-tier value and sorts strictly below every real tier
/// (and is distinct from <see cref="Tolerated"/> = −1).
/// </para>
/// <para>
/// Canonical type for the whole solution (#368/#370): Smaug and Arwen previously
/// maintained local string-based ladders — both now converge here (Smaug #370 Task 3,
/// Arwen Task 2). The slim <c>NpcService.MinFavorTier</c> is <c>FavorTier?</c> as of
/// #385, parsed once at the <c>ReferenceDataService</c> projection via <see cref="Parse"/>
/// (junk → <see cref="Unknown"/>); downstream comparisons are typed.
/// </para>
/// </remarks>
public enum FavorTier
{
    /// <summary>Token absent or not a known tier. Sorts below every real tier;
    /// never equals a real rank.</summary>
    Unknown = int.MinValue,

    Despised = -4,
    Hated = -3,
    Disliked = -2,
    Tolerated = -1,
    Neutral = 0,
    Comfortable = 1,
    Friends = 2,
    CloseFriends = 3,
    BestFriends = 4,
    LikeFamily = 5,
    SoulMates = 6,
}

/// <summary>Parsing / display / round-trip helpers for <see cref="FavorTier"/>.</summary>
public static class FavorTierExtensions
{
    /// <summary>
    /// Maps a raw reference-data tier token (e.g. <c>"LikeFamily"</c>) to a
    /// <see cref="FavorTier"/>. Case-insensitive. Blank or unrecognised → <see
    /// cref="FavorTier.Unknown"/> (never throws). A numeric string is rejected
    /// (so a stray <c>"-4"</c> does not silently become <c>Despised</c>).
    /// </summary>
    public static FavorTier Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return FavorTier.Unknown;
        if (Enum.TryParse<FavorTier>(token, ignoreCase: true, out var t)
            && Enum.IsDefined(t)
            && !char.IsDigit(token[0]) && token[0] != '-')
        {
            return t;
        }
        return FavorTier.Unknown;
    }

    /// <summary>
    /// The exact reference-data token for a tier (the enum member name;
    /// <see cref="FavorTier.Unknown"/> → <c>"Unknown"</c>). Used to key
    /// calibration data (string-keyed, persisted) and anywhere a raw token string
    /// is required instead of the typed enum.
    /// </summary>
    public static string ToToken(this FavorTier tier) => tier.ToString();

    /// <summary>
    /// Human-friendly label. Curated because <em>every</em> tier token is absent
    /// from the game's <c>strings_all.json</c> (verified) — there is no upstream
    /// localization to resolve through, so this map is the source of truth.
    /// </summary>
    public static string DisplayName(this FavorTier tier) => tier switch
    {
        FavorTier.CloseFriends => "Close Friends",
        FavorTier.BestFriends => "Best Friends",
        FavorTier.LikeFamily => "Like Family",
        FavorTier.SoulMates => "Soul Mates",
        FavorTier.Unknown => "Unknown",
        _ => tier.ToString(),
    };
}
