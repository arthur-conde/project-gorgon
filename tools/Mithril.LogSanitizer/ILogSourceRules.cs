namespace Mithril.Tools.LogSanitizer;

/// <summary>
/// Per-log-source name-discovery rules. Pass-1 scans every line through
/// <see cref="DiscoverNames"/> to build the <see cref="NameRegistry"/>
/// pass-2 substring-replaces all collected names.
/// </summary>
public interface ILogSourceRules
{
    /// <summary>
    /// Inspects a single line and registers any names it finds with the registry.
    /// Implementations should be cheap (called once per line during pass 1).
    /// </summary>
    void DiscoverNames(string line, NameRegistry registry);
}
