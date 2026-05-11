namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Machine context captured once per perf-trace session and written as the
/// first line of the trace file. Without it, the JSON-lines stream is hard
/// to interpret cross-machine (60Hz vs 144Hz frame budget, DPI affects WPF
/// layout cost, build version pins which fix/regression the trace covers).
/// </summary>
public sealed record SessionHeader(
    string Build,
    string Os,
    string Gpu,
    int RefreshRateHz,
    double Dpi,
    string? ActiveCharacter,
    string? ActiveServer,
    IReadOnlyList<string> LoadedModules);
