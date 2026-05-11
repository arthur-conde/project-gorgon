namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Machine context captured once per perf-trace session and written as the
/// first line of the trace file. Without it, the JSON-lines stream is hard
/// to interpret cross-machine (60Hz vs 144Hz frame budget, DPI affects WPF
/// layout cost, build version pins which fix/regression the trace covers).
///
/// The three render fields (<see cref="RenderTier"/>, <see cref="RenderMode"/>,
/// <see cref="IsRemoteSession"/>) disambiguate "stall on a tier-0 box" from
/// "real GPU/DWM event" — these are different bugs with different fixes.
/// Without them, every stall-with-idle-dispatcher looks the same.
/// </summary>
public sealed record SessionHeader(
    string Build,
    string Os,
    string Gpu,
    int RefreshRateHz,
    double Dpi,
    string? ActiveCharacter,
    string? ActiveServer,
    IReadOnlyList<string> LoadedModules,
    int RenderTier,
    string RenderMode,
    bool IsRemoteSession);
