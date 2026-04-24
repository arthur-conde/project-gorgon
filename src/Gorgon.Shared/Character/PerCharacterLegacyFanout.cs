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
    /// <see cref="PerCharacterStore{T}.Save"/>ing the result. If <paramref name="view"/> is
    /// supplied and at least one write succeeded, invalidates its cache so a previously
    /// loaded empty/stale <typeparamref name="TPerChar"/> can't be flushed over the
    /// fresh on-disk data on the next character switch, and subscribers re-read.
    /// </summary>
    /// <returns>Names whose server could not be resolved (skipped — try again next startup).</returns>
    public static IReadOnlyList<string> FanOut<TPerChar>(
        IEnumerable<string> names,
        PerCharacterStore<TPerChar> store,
        IActiveCharacterService active,
        Func<string, TPerChar> extractFor,
        PerCharacterView<TPerChar>? view = null,
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
        var wroteAny = false;
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
                wroteAny = true;
                diag?.Info("LegacyFanout", $"Wrote {path}");
            }
            catch (Exception ex)
            {
                diag?.Warn("LegacyFanout", $"Failed to split {name}: {ex.Message}");
                unresolved.Add(name);
            }
        }

        if (wroteAny) view?.Invalidate();
        return unresolved;
    }
}
