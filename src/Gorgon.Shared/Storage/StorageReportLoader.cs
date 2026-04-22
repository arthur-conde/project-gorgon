using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gorgon.Shared.Storage;

/// <summary>
/// Loads and transforms character storage export JSON files.
/// </summary>
public static partial class StorageReportLoader
{
    /// <summary>Deserialize a storage export JSON file.</summary>
    public static StorageReport Load(string filePath)
    {
        var json = File.ReadAllBytes(filePath);
        return JsonSerializer.Deserialize(json, StorageReportJsonContext.Default.StorageReport)
               ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");
    }

    /// <summary>Scan a directory for storage export files, newest first.</summary>
    public static IReadOnlyList<ReportFileInfo> ScanForReports(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var results = new List<ReportFileInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*_items_*.json"))
        {
            var match = FileNamePattern().Match(Path.GetFileNameWithoutExtension(file));
            if (!match.Success) continue;

            results.Add(new ReportFileInfo(
                file,
                match.Groups["char"].Value,
                match.Groups["server"].Value,
                File.GetLastWriteTimeUtc(file)));
        }

        results.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
        return results;
    }

    /// <summary>Normalize StorageVault names into human-readable location strings.</summary>
    public static string NormalizeLocation(string? storageVault, bool isInInventory)
    {
        if (isInInventory || storageVault is null)
            return "Inventory";

        if (storageVault.StartsWith("NPC_", StringComparison.Ordinal))
        {
            var npcName = storageVault[4..]; // strip "NPC_"
            return "NPC: " + InsertSpaces().Replace(npcName, " $1");
        }

        if (storageVault.StartsWith('*'))
        {
            // *AccountStorage_Serbule → Account: Serbule
            var parts = storageVault[1..].Split('_', 2);
            return parts.Length == 2 ? $"Account: {parts[1]}" : storageVault[1..];
        }

        // CouncilVault → Council Vault, StorageCrate → Storage Crate
        return InsertSpaces().Replace(storageVault, " $1").TrimStart();
    }

    [GeneratedRegex(@"(?<char>.+?)_(?<server>[^_]+)_items_")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"(?<=[a-z])([A-Z])")]
    private static partial Regex InsertSpaces();
}

/// <summary>Metadata about a discovered storage export file.</summary>
public sealed record ReportFileInfo(
    string FilePath,
    string Character,
    string Server,
    DateTime LastModifiedUtc)
{
    public override string ToString() =>
        $"{Character} ({Server}) — {LastModifiedUtc.ToLocalTime():MMM d, HH:mm}";
}
