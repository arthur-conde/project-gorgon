namespace Mithril.WorldSim;

/// <summary>
/// Internal frame payload that drives a world's clock-tick path. Carries only
/// the source-stream timestamp; the owned <see cref="IFolder{TPayload}"/>
/// converts it into a <see cref="CalendarTimeAdvanced"/> change event
/// (deduplicated within a wall-clock second per principle 13).
///
/// <para><b>Why it exists.</b> Without a named clock-tick path, the world's
/// clock would advance only when some other folder's payload is dispatched.
/// During a stretch of source-stream lines that no folder cares about, the
/// clock would stagnate and Gandalf's scheduler-collapse alarms (migration
/// item #12) would fire late. This payload makes the clock-tick path
/// explicit, owned, and testable — see <a href="https://github.com/moumantai-gg/mithril/issues/644">#644</a>
/// and <a href="https://github.com/moumantai-gg/mithril/issues/655">#655</a>.</para>
///
/// <para><b>Why a struct.</b> One frame per source-stream envelope is the
/// hottest single allocation in the world simulator; a value-type payload
/// keeps the per-tick cost to the boxing the bus already does at publish
/// time (one allocation per tick) rather than two.</para>
///
/// <para><b>Internal to the producer/folder pair.</b> Subscribers consume
/// <see cref="CalendarTimeAdvanced"/> on the bus; <see cref="WorldClockTick"/>
/// itself is not bus-surfaced (the folder swallows it). The type is
/// <c>public</c> only so per-world producer + folder implementations across
/// assemblies can reference the same payload contract.</para>
/// </summary>
/// <param name="At">Simulated wall-clock at this tick — the source-stream
/// envelope's timestamp.</param>
public readonly record struct WorldClockTick(DateTimeOffset At);
