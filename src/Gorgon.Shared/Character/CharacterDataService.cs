using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Game;

namespace Gorgon.Shared.Character;

public sealed partial class CharacterDataService : ICharacterDataService
{
    private readonly GameConfig _gameConfig;
    private readonly IDiagnosticsSink? _diag;
    private FileSystemWatcher? _watcher;

    private IReadOnlyList<CharacterSnapshot> _characters = [];
    private CharacterSnapshot? _activeCharacter;

    public CharacterDataService(GameConfig gameConfig, IDiagnosticsSink? diag = null)
    {
        _gameConfig = gameConfig;
        _diag = diag;

        _gameConfig.PropertyChanged += OnGameConfigChanged;
        Refresh();
    }

    public IReadOnlyList<CharacterSnapshot> Characters => _characters;

    public CharacterSnapshot? ActiveCharacter => _activeCharacter;

    public event EventHandler? CharactersChanged;

    public void SetActiveCharacter(string name, string server)
    {
        _activeCharacter = _characters.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            c.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
    }

    public void Refresh()
    {
        var dir = _gameConfig.ReportsDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _characters = [];
            _diag?.Info("Character", "Reports directory not found; no characters loaded.");
            RebuildWatcher(dir);
            CharactersChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var snapshots = new List<CharacterSnapshot>();
        foreach (var file in Directory.EnumerateFiles(dir, "Character_*.json"))
        {
            var snap = TryParse(file);
            if (snap is not null) snapshots.Add(snap);
        }

        snapshots.Sort((a, b) => b.ExportedAt.CompareTo(a.ExportedAt));
        _characters = snapshots;

        // Auto-select the most recent if nothing selected, or re-match the active character.
        if (_activeCharacter is not null)
        {
            _activeCharacter = snapshots.FirstOrDefault(c =>
                c.Name.Equals(_activeCharacter.Name, StringComparison.OrdinalIgnoreCase) &&
                c.Server.Equals(_activeCharacter.Server, StringComparison.OrdinalIgnoreCase));
        }
        _activeCharacter ??= snapshots.FirstOrDefault();

        _diag?.Info("Character", $"Loaded {snapshots.Count} character export(s) from {dir}.");
        RebuildWatcher(dir);
        CharactersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _gameConfig.PropertyChanged -= OnGameConfigChanged;
        _watcher?.Dispose();
        _watcher = null;
    }

    private CharacterSnapshot? TryParse(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize(stream, CharacterJsonContext.Default.RawCharacterExport);
            if (raw is null || raw.Report != "CharacterSheet") return null;

            var skills = new Dictionary<string, CharacterSkill>(StringComparer.Ordinal);
            if (raw.Skills is not null)
            {
                foreach (var (key, v) in raw.Skills)
                {
                    skills[key] = new CharacterSkill(
                        v.Level ?? 0,
                        v.BonusLevels ?? 0,
                        v.XpTowardNextLevel ?? 0,
                        v.XpNeededForNextLevel ?? 0);
                }
            }

            DateTimeOffset exported = default;
            if (raw.Timestamp is not null)
            {
                DateTimeOffset.TryParse(raw.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out exported);
            }

            var npcFavor = new Dictionary<string, string>(StringComparer.Ordinal);
            if (raw.NPCs is not null)
            {
                foreach (var (key, v) in raw.NPCs)
                    if (!string.IsNullOrEmpty(v.FavorLevel))
                        npcFavor[key] = v.FavorLevel;
            }

            return new CharacterSnapshot(
                raw.Character ?? Path.GetFileNameWithoutExtension(path),
                raw.ServerName ?? "",
                exported,
                skills,
                raw.RecipeCompletions ?? new Dictionary<string, int>(),
                npcFavor);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Character", $"Failed to parse {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void RebuildWatcher(string? dir)
    {
        _watcher?.Dispose();
        _watcher = null;

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            _watcher = new FileSystemWatcher(dir, "Character_*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception ex)
        {
            _diag?.Warn("Character", $"FileSystemWatcher setup failed: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Refresh();

    private void OnGameConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameConfig.ReportsDirectory))
            Refresh();
    }
}
