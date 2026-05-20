namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) line-local pure classifier. Hand-parsed, no regex, no
/// per-line allocation — runs in the L0.5 classifier's hot path which
/// sits on the same thread that ingested the line from disk (G6
/// pre-channel discard constraint). Same allocation-free contract as
/// <see cref="PlayerLogClock.TryParseTimestampPrefix"/>.
///
/// <para>This type is the pure classify function; the orchestrating
/// component that subscribes to L0, calls <see cref="Classify"/> on each
/// line, and publishes the result on the unified pipe is
/// <see cref="PlayerLogClassifier"/> (#556).</para>
///
/// <para><b>Grammar-keyed, not token-keyed.</b> Classification matches the
/// full <c>[ts] actor: Verb(args)</c> shape, never the actor token in
/// isolation. The <c>entity_</c> prefix spans three orthogonal buckets in
/// the corpus (combat-actor / <c>_skin</c> teardown / nav-mesh diagnostic) —
/// a token-prefix shortcut would silently bleed the combat pipe into
/// discard. See the <c>prefix-collision-onattack</c> and
/// <c>entity-skin-teardown</c> / <c>entity-navmesh-nots</c> regression
/// fixtures.</para>
/// </summary>
internal static class PlayerLogLineClassifier
{
    /// <summary>
    /// Classification verdict + the offset into the source line where the
    /// post-envelope payload begins. Returned by value as a small struct so
    /// the classifier remains allocation-free.
    /// </summary>
    public readonly record struct Result(
        LineKind Kind,
        int DataStart,
        long CombatEntityId,
        SystemSignalKind SystemKind);

    /// <summary>
    /// Classify <paramref name="line"/>. <see cref="Result.DataStart"/> is the
    /// index of the first character of the payload (caller does
    /// <c>line.Substring(DataStart)</c> only on the surviving ~5%) or <c>-1</c>
    /// when the kind does not surface a typed payload (<see cref="LineKind.Discard"/>,
    /// <see cref="LineKind.Anomaly"/>).
    /// </summary>
    public static Result Classify(string line)
    {
        // Defensive guard for the empty-line case the tailer trims out, but
        // a fixture or synthetic input might still pass.
        if (string.IsNullOrEmpty(line)) return new Result(LineKind.Discard, -1, 0, default);

        // --- Cheap-discard rules that don't depend on a `[ts]` prefix ---
        // Order matters only as a hot-path optimisation; the buckets are
        // disjoint by design (verified by the per-rule fixtures).

        // Blank / whitespace-only line.
        if (IsAllWhitespace(line)) return new Result(LineKind.Discard, -1, 0, default);

        // Any indented (leading-whitespace) line: stack frames in any
        // namespace, header-continuation blocks (Direct3D: + indented
        // Version: / Renderer: / Vendor: / VRAM: / Driver:). Catches both
        // gaps with one rule; safe because no real signal line is indented.
        if (line[0] == ' ' || line[0] == '\t') return new Result(LineKind.Discard, -1, 0, default);

        // Native-address frames `0x[0-9a-fA-F]+ (`. Live data shows these
        // sit before the first [ts] line (preamble territory, handled by
        // #514's seed), so this rule is rarely if ever exercised by L0.5
        // post-preamble — kept defensively for the straddle case.
        if (line[0] == '0' && line.Length >= 4 && line[1] == 'x' && IsHexDigit(line[2]))
        {
            // walk forward looking for " ("
            for (var i = 3; i < line.Length - 1 && i < 24; i++)
            {
                if (IsHexDigit(line[i])) continue;
                if (line[i] == ' ' && line[i + 1] == '(') return new Result(LineKind.Discard, -1, 0, default);
                break;
            }
        }

        // Engine subsystem brackets `[Word…]` with NO timestamp prefix.
        // The `[HH:MM:SS]` timestamp shape has digits at positions 1/2,
        // colons at 3/6, etc., so this is unambiguous against a real
        // timestamped line: a bracket whose interior starts with an
        // ASCII letter is a subsystem tag (e.g. `[Physics::Module]`,
        // `[D3D12 Device Filter]`, `[Subsystems]`, `[Vulkan::Renderer]`).
        if (line[0] == '[' && line.Length >= 3 && IsAsciiLetter(line[1]))
        {
            return new Result(LineKind.Discard, -1, 0, default);
        }

        // EVENT(Ok): lifecycle phases — no [ts], system-signal pipe.
        // Verify the EVENT( prefix and (Ok) parens, then look at the verb
        // after the colon-space.
        if (StartsWithLiteral(line, "EVENT(Ok): "))
        {
            const int dataStart = 11; // length of "EVENT(Ok): "
            // The lifecycle phases we route: loginCharacter | playing | sessionUpdate.
            // (The pre-login preamble's EVENT(Ok): connected sits outside the
            // L0 replay window and is #514's seed-facility territory.)
            if (StartsWithLiteral(line, "EVENT(Ok): loginCharacter")
                || StartsWithLiteral(line, "EVENT(Ok): playing")
                || StartsWithLiteral(line, "EVENT(Ok): sessionUpdate"))
            {
                return new Result(LineKind.SystemSignal, dataStart, 0, SystemSignalKind.SessionLifecycle);
            }
            // EVENT(Ok): connected and any other unknown phase falls to anomaly.
            return new Result(LineKind.Anomaly, -1, 0, default);
        }

        // Standalone engine diagnostics that aren't bracketed but are still
        // pure noise. Each kept as its own literal so a refactor that adds
        // a sibling rule doesn't unintentionally absorb new shapes.
        if (StartsWithLiteral(line, "ClearCursor(SelectionController)"))
            return new Result(LineKind.Discard, -1, 0, default);

        // Asset / loader noise. PG emits these with AND without a `[ts] `
        // prefix in the same session — accept either by checking at offset 0
        // and (when present) at offset 11.
        if (IsAssetLoaderNoise(line, 0)) return new Result(LineKind.Discard, -1, 0, default);
        if (PlayerLogClock.TryParseTimestampPrefix(line, out _)
            && IsAssetLoaderNoise(line, 11))
        {
            return new Result(LineKind.Discard, -1, 0, default);
        }

        // `Direct3D:` is the header of a deterministic 6-line block; the five
        // indented continuation lines are caught by the indented rule above,
        // but the header itself is no-ts and not bracketed.
        if (StartsWithLiteral(line, "Direct3D:")) return new Result(LineKind.Discard, -1, 0, default);

        // entity_<id>_skin : destroying … — Unity renderer asset teardown
        // (no-ts, prefix-collides with the `entity_<id>:` combat actor).
        if (StartsWithLiteral(line, "entity_") && IsEntitySkinTeardown(line))
            return new Result(LineKind.Discard, -1, 0, default);

        // entity_<id>: Not on nav mesh! — Unity AI engine diagnostic (no-ts,
        // also a prefix-collision shape).
        if (StartsWithLiteral(line, "entity_") && IsEntityNavMeshNoTs(line))
            return new Result(LineKind.Discard, -1, 0, default);

        // Exception headers like `InvalidOperationException:`, `NullReferenceException:`,
        // and the bespoke `The referenced script on this Behaviour …` (no-ts).
        if (IsExceptionHeader(line)) return new Result(LineKind.Discard, -1, 0, default);

        // --- Timestamped lines from here on ---
        if (!PlayerLogClock.TryParseTimestampPrefix(line, out _))
        {
            // No timestamp + no discard rule matched → anomaly.
            return new Result(LineKind.Anomaly, -1, 0, default);
        }

        // Post-prefix slice starts at index 11 (`[HH:MM:SS] `).
        const int prefixLen = 11;
        if (line.Length <= prefixLen) return new Result(LineKind.Anomaly, -1, 0, default);

        // LOADING LEVEL <Area>
        if (StartsAt(line, prefixLen, "LOADING LEVEL "))
        {
            return new Result(LineKind.SystemSignal, prefixLen, 0, SystemSignalKind.AreaLoading);
        }

        // Logged in as character <Name>. Time UTC=… Timezone Offset …
        if (StartsAt(line, prefixLen, "Logged in as character "))
        {
            return new Result(LineKind.SystemSignal, prefixLen, 0, SystemSignalKind.LoginBanner);
        }

        // [ts] LocalPlayer: …
        if (StartsAt(line, prefixLen, "LocalPlayer: "))
        {
            var dataStart = prefixLen + "LocalPlayer: ".Length;
            // ProcessAddPlayer is system-signal (login completion), not
            // LocalPlayer pipe — even though it shares the LocalPlayer:
            // envelope. Routed before the generic Process* check.
            if (StartsAt(line, dataStart, "ProcessAddPlayer("))
            {
                return new Result(LineKind.SystemSignal, dataStart, 0, SystemSignalKind.PlayerAdded);
            }
            // Generic LocalPlayer pipe: any Process<Verb>(…
            if (StartsAt(line, dataStart, "Process"))
            {
                return new Result(LineKind.LocalPlayer, dataStart, 0, default);
            }
            // Unknown LocalPlayer: <verb-not-Process*> is anomaly — the
            // corpus has no such shape; surfacing it lets G7 telemetry
            // catch a PG patch that introduces a non-Process actor verb.
            return new Result(LineKind.Anomaly, -1, 0, default);
        }

        // [ts] entity_<id>: …
        if (StartsAt(line, prefixLen, "entity_"))
        {
            var idStart = prefixLen + "entity_".Length;
            if (TryParseEntityIdAndColon(line, idStart, out var entityId, out var dataStart))
            {
                // The same `entity_<id>:` token can appear with a non-actor
                // body when the line is a [ts]-prefixed Unity AI diagnostic
                // (`entity_<id>: Not on nav mesh!`). Combat-actor lines
                // start with `On<Verb>(` — anything else under the actor
                // envelope falls to anomaly so PG verb-space drift surfaces.
                if (StartsAt(line, dataStart, "On") && dataStart + 2 < line.Length
                    && IsAsciiUpperLetter(line[dataStart + 2]))
                {
                    return new Result(LineKind.CombatActor, dataStart, entityId, default);
                }
                // [ts] entity_<id>: Not on nav mesh! — non-actor diagnostic.
                if (StartsAt(line, dataStart, "Not on nav mesh"))
                    return new Result(LineKind.Discard, -1, 0, default);
                return new Result(LineKind.Anomaly, -1, 0, default);
            }
            // entity_<garbage>: didn't parse as an integer id — anomaly.
            return new Result(LineKind.Anomaly, -1, 0, default);
        }

        // [ts] <something else>: catches any other actor token. Anomaly
        // routing lets G7 telemetry catch new actor tokens introduced by PG.
        return new Result(LineKind.Anomaly, -1, 0, default);
    }

    public enum LineKind
    {
        /// <summary><c>[ts] LocalPlayer: Process*(…)</c> — typed L0.5 pipe.</summary>
        LocalPlayer,
        /// <summary><c>[ts] entity_&lt;id&gt;: On*(…)</c> — reserved combat pipe.</summary>
        CombatActor,
        /// <summary>Small set of session-level non-actor signals (see <see cref="SystemSignalKind"/>).</summary>
        SystemSignal,
        /// <summary>Engine/asset/exception noise. ~95% of the corpus.</summary>
        Discard,
        /// <summary>Genuinely unknown shape. Counted + sampled via diagnostics; never asserts.</summary>
        Anomaly,
    }

    // --- helpers ---

    private static bool IsAllWhitespace(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != ' ' && c != '\t' && c != '\r') return false;
        }
        return true;
    }

    private static bool IsAsciiDigit(char c) => (uint)(c - '0') <= 9;
    private static bool IsHexDigit(char c) => IsAsciiDigit(c) || (uint)(c - 'a') <= 5 || (uint)(c - 'A') <= 5;
    private static bool IsAsciiLetter(char c) => ((uint)(c - 'a') <= 25) || ((uint)(c - 'A') <= 25);
    private static bool IsAsciiUpperLetter(char c) => (uint)(c - 'A') <= 25;

    private static bool StartsWithLiteral(string line, string literal)
    {
        if (line.Length < literal.Length) return false;
        for (var i = 0; i < literal.Length; i++)
            if (line[i] != literal[i]) return false;
        return true;
    }

    private static bool StartsAt(string line, int offset, string literal)
    {
        if (line.Length - offset < literal.Length) return false;
        for (var i = 0; i < literal.Length; i++)
            if (line[offset + i] != literal[i]) return false;
        return true;
    }

    private static bool IsAssetLoaderNoise(string line, int offset)
    {
        // Centralised list so the no-ts and `[ts] `-prefixed callsites stay
        // in sync. Each literal is a deterministic engine/asset-loader shape
        // confirmed in the merged-corpus fixture.
        return StartsAt(line, offset, "Download")
            || StartsAt(line, offset, "LoadAssetAsync:")
            || StartsAt(line, offset, "IsDoneLoading:")
            || StartsAt(line, offset, "Ref-count")
            || StartsAt(line, offset, "Completed ")
            || StartsAt(line, offset, "Successfully ")
            || StartsAt(line, offset, "Uploading ")
            || StartsAt(line, offset, "UnloadTime:");
    }

    private static bool IsEntitySkinTeardown(string line)
    {
        // entity_<id>_skin : destroying ...
        // Walk past the digits, expect "_skin : destroying".
        var i = "entity_".Length;
        if (i >= line.Length || !IsAsciiDigit(line[i])) return false;
        while (i < line.Length && IsAsciiDigit(line[i])) i++;
        return StartsAt(line, i, "_skin : destroying");
    }

    private static bool IsEntityNavMeshNoTs(string line)
    {
        // entity_<id>: Not on nav mesh!  (no [ts] prefix)
        var i = "entity_".Length;
        if (i >= line.Length || !IsAsciiDigit(line[i])) return false;
        while (i < line.Length && IsAsciiDigit(line[i])) i++;
        return StartsAt(line, i, ": Not on nav mesh");
    }

    private static bool IsExceptionHeader(string line)
    {
        // `<UppercaseName>Exception: ` or `<UppercaseName>Exception <...>`
        // followed by indented stack frames (the indented rule above catches
        // the frames; this rule catches the header).
        // Also catch the specific `The referenced script on this Behaviour…`
        // shape Unity emits for missing-script renderer warnings.
        if (StartsWithLiteral(line, "The referenced script on this Behaviour")) return true;

        // Look for "Exception" anywhere up to a ': ' or ' '. Bounded so the
        // scan stays cheap (real exception names are < ~64 chars).
        if (!IsAsciiUpperLetter(line[0])) return false;
        var cap = Math.Min(line.Length, 80);
        for (var i = 1; i < cap; i++)
        {
            var c = line[i];
            if (IsAsciiLetter(c)) continue;
            // First non-letter: must be `Exception` ending at position i,
            // followed by `:` or end-of-relevant-prefix.
            const string suffix = "Exception";
            if (i < suffix.Length) return false;
            if (!StartsAt(line, i - suffix.Length, suffix)) return false;
            return c == ':' || c == ' ';
        }
        return false;
    }

    private static bool TryParseEntityIdAndColon(string line, int idStart, out long entityId, out int dataStart)
    {
        entityId = 0;
        dataStart = -1;
        var i = idStart;
        if (i >= line.Length || !IsAsciiDigit(line[i])) return false;
        long acc = 0;
        while (i < line.Length && IsAsciiDigit(line[i]))
        {
            // Bound the accumulator to avoid pathological overflow on a
            // malformed token; PG entity ids are < 2^31 in practice but
            // long-typed defensively.
            acc = acc * 10 + (line[i] - '0');
            if (acc < 0) return false;
            i++;
        }
        // Need ": " right after the digits to be a real actor envelope.
        if (i + 1 >= line.Length || line[i] != ':' || line[i + 1] != ' ') return false;
        entityId = acc;
        dataStart = i + 2;
        return true;
    }
}
