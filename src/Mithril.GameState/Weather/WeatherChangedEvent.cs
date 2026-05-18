using Mithril.Shared.Logging;

namespace Mithril.GameState.Weather;

/// <summary>
/// One parsed <c>LocalPlayer: ProcessSetWeather("&lt;condition&gt;", &lt;flag&gt;)</c>
/// line. The pure line→event output of <see cref="WeatherLogParser"/>;
/// <see cref="PlayerWeatherTracker"/> folds a stream of these into the
/// last-known weather state.
///
/// <para>Carries a <see cref="LogEvent"/>-mandated <see cref="DateTime"/>
/// timestamp (Player.log <c>[HH:MM:SS]</c> is UTC). It is converted to a
/// <see cref="DateTimeOffset"/> at the tracker boundary — model/notification
/// surfaces use <see cref="DateTimeOffset"/>; the parser interface is not
/// widened (mirrors <c>MapPinLogEvent</c>).</para>
///
/// <para><b>Why this exists.</b> Project Gorgon's <c>Vampirism</c> skill makes
/// the player take damage in sunlight; the ambient weather condition gates
/// whether that is happening. This event is the raw, faithful signal. It does
/// <em>not</em> interpret which conditions are "sunny" / damaging — that
/// classification is a consuming feature's job (the same dumb-producer split
/// as <c>MapPinLogEvent</c> carrying a raw <c>RawList</c> arg).</para>
/// </summary>
/// <param name="Timestamp">The source line's reconstructed UTC instant
/// (Player.log <c>[HH:MM:SS]</c> is UTC).</param>
/// <param name="Condition">The weather condition string verbatim from the
/// first argument (e.g. <c>"Foggy"</c>). Surfaced unmodified — no normalisation
/// or sunny/overcast classification here.</param>
/// <param name="Flag">The line's opaque second boolean argument, passed
/// through unmodified (the same convention as <c>MapPinLogEvent.RawList</c>).
/// <b>Semantics unverified — Verification owed:</b> from the single captured
/// sample <c>ProcessSetWeather("Foggy", True)</c> the meaning is unknown
/// (plausibly a crossfade-vs-instant flag, or a login-resync vs. live-change
/// flag — neither confirmed against a live corpus). Consumers should not depend
/// on an interpretation of this bit until the grammar is characterised.</param>
public sealed record WeatherChangedEvent(
    DateTime Timestamp,
    string Condition,
    bool Flag) : LogEvent(Timestamp);
