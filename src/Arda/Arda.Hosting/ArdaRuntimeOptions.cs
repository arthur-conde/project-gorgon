namespace Arda.Hosting;

/// <summary>
/// Runtime-only knobs derived from environment / process state, not from
/// <see cref="ArdaOptions"/> (which is user-supplied configuration). Resolved
/// once during <c>AddArda</c> and injected into the driver hosted services.
/// </summary>
public sealed record ArdaRuntimeOptions(bool TolerantGrammar);
