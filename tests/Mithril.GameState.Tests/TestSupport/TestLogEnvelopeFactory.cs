using Mithril.Shared.Logging;

namespace Mithril.GameState.Tests.TestSupport;

/// <summary>
/// Shared helper for the archetype-A producer tests (post-#550 PR 2 +
/// PR #555 review): one source of truth for building
/// <see cref="LocalPlayerLogLine"/> envelopes from full
/// <c>[ts] LocalPlayer: …</c> Player.log shapes — equivalent to the
/// strip work L0.5 (#532) does on real input.
///
/// <para>Replaces four near-identical per-file <c>MakeLocal</c> /
/// <c>ToLocal</c> helpers (Celestial / Quest / Recipe / Skill service tests)
/// that subtly diverged: hard-coded envelopes, leading-bracket-only checks,
/// or shape-specific substring matches. This helper converges them on one
/// semantics so a stripper-bug fix only happens once.</para>
/// </summary>
internal static class TestLogEnvelopeFactory
{
    private const int TsPrefixLen = 11;             // length of "[HH:MM:SS] "
    private const string ActorToken = "LocalPlayer: ";

    /// <summary>
    /// Build a <see cref="LocalPlayerLogLine"/> directly from a verb-only
    /// payload (already envelope-stripped). Use when the test owns the data
    /// shape and doesn't need stripping.
    /// </summary>
    public static LocalPlayerLogLine MakeLocalPlayer(
        string data,
        DateTime? timestamp = null,
        long sequence = 0,
        long readMonotonicTicks = 0) =>
        new(new DateTimeOffset(timestamp ?? DateTime.UtcNow, TimeSpan.Zero),
            data,
            sequence,
            readMonotonicTicks);

    /// <summary>
    /// Build a <see cref="LocalPlayerLogLine"/> from a full Player.log line
    /// shape (<c>[HH:MM:SS] LocalPlayer: Verb(args)</c>). Mimics what the
    /// real L0.5 router does on incoming raw lines: peel the optional
    /// <c>[ts]</c> prefix and the mandatory <c>LocalPlayer:</c> actor token,
    /// leave the rest as <c>Data</c>. Tolerates lines missing either prefix.
    /// </summary>
    public static LocalPlayerLogLine FromRawLine(
        string fullLine,
        DateTime? timestamp = null,
        long sequence = 0,
        long readMonotonicTicks = 0) =>
        new(new DateTimeOffset(timestamp ?? DateTime.UtcNow, TimeSpan.Zero),
            StripEnvelope(fullLine),
            sequence,
            readMonotonicTicks);

    /// <summary>
    /// Build a <see cref="LocalPlayerLogLine"/> from a <see cref="RawLogLine"/>
    /// (preserves the raw's Timestamp / Sequence / ReadMonotonicTicks fields).
    /// </summary>
    public static LocalPlayerLogLine FromRawLine(RawLogLine raw) =>
        new(raw.Timestamp, StripEnvelope(raw.Line), raw.Sequence, raw.ReadMonotonicTicks);

    /// <summary>
    /// Strip the optional <c>[HH:MM:SS]</c> timestamp prefix and the
    /// mandatory <c>LocalPlayer:</c> actor envelope from a Player.log
    /// line, returning the bare verb payload. Returns the input unchanged
    /// when neither prefix is present.
    /// </summary>
    private static string StripEnvelope(string line)
    {
        var idx = 0;
        if (line.Length > TsPrefixLen
            && line[0] == '['
            && line[3] == ':'
            && line[6] == ':'
            && line[9] == ']')
        {
            idx = TsPrefixLen;
        }
        if (idx + ActorToken.Length <= line.Length
            && line.IndexOf(ActorToken, idx, StringComparison.Ordinal) == idx)
        {
            idx += ActorToken.Length;
        }
        return idx == 0 ? line : line.Substring(idx);
    }
}
