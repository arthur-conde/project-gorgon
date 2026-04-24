using System.IO;
using Gorgon.Shared.Diagnostics;

namespace Gorgon.Shared.Character;

/// <summary>
/// Helper for one-shot migrations that split a legacy <c>Dictionary&lt;CharacterName, …&gt;</c>
/// blob into per-character files. The caller reads the legacy payload, extracts the character
/// names, and hands the helper a per-name extractor. The helper resolves each name to its
/// server (via <see cref="IActiveCharacterService.Characters"/> or the active character),
/// writes the per-character file via <see cref="PerCharacterStore{T}"/>, and reports which
/// characters it couldn't resolve (e.g. no export on disk). The caller decides whether to
/// delete/trim the legacy source based on whether every character resolved.
/// </summary>
public static class PerCharacterLegacyFanout
{
    /// <summary>
    /// Writes a per-character file for each name in <paramref name="names"/> whose server
    /// can be resolved, by calling <paramref name="extractFor"/> for that name and
    /// <see cref="PerCharacterStore{T}.Save"/>ing the result.
    /// </summary>
    /// <returns>Names whose server could not be resolved (skipped — try again next startup).</returns>
    public static IReadOnlyList<string> FanOut<TPerChar>(
        IEnumerable<string> names,
        PerCharacterStore<TPerChar> store,
        IActiveCharacterService active,
        Func<string, TPerChar> extractFor,
        IDiagnosticsSink? diag = null)
        where TPerChar : class, IVersionedState<TPerChar>, new()
    {
        var knownServers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in active.Characters)
        {
            if (!knownServers.ContainsKey(snapshot.Name)) knownServers[snapshot.Name] = snapshot.Server;
        }
        if (!string.IsNullOrEmpty(active.ActiveCharacterName) && !string.IsNullOrEmpty(active.ActiveServer))
            knownServers[active.ActiveCharacterName] = active.ActiveServer;

        var unresolved = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!knownServers.TryGetValue(name, out var server))
            {
                unresolved.Add(name);
                continue;
            }
            var path = store.GetFilePath(name, server);
            if (File.Exists(path))
            {
                // Target already exists: we do NOT clobber it. This protects a real per-char
                // file from being overwritten by a later re-run of a legacy blob that still
                // happens to list the same character. Log so the silent skip is greppable —
                // a surprising drop would otherwise be invisible.
                diag?.Warn("LegacyFanout",
                    $"Per-char file {path} already exists; legacy slice for {name} dropped.");
                continue;
            }
            try
            {
                var perChar = extractFor(name);
                store.Save(name, server, perChar);
                diag?.Info("LegacyFanout", $"Wrote {path}");
            }
            catch (Exception ex)
            {
                diag?.Warn("LegacyFanout", $"Failed to split {name}: {ex.Message}");
                unresolved.Add(name);
            }
        }
        return unresolved;
    }
}
