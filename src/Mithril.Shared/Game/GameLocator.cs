using System.IO;

namespace Mithril.Shared.Game;

public static class GameLocator
{
    public static string? AutoDetectGameRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        var candidate = Path.Combine(
            Directory.GetParent(appData)?.FullName ?? "",
            "LocalLow", "Elder Game", "Project Gorgon");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
