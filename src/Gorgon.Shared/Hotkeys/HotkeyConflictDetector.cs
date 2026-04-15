namespace Gorgon.Shared.Hotkeys;

public sealed record ConflictInfo(string ConflictingCommandId);

public static class HotkeyConflictDetector
{
    /// <summary>
    /// Returns commandId → ConflictInfo for every binding that collides with another.
    /// </summary>
    public static Dictionary<string, ConflictInfo> Detect(IEnumerable<HotkeyBinding> bindings)
    {
        var result = new Dictionary<string, ConflictInfo>(StringComparer.Ordinal);
        var byCombo = new Dictionary<(uint, HotkeyModifiers), string>();
        foreach (var b in bindings)
        {
            var key = (b.VirtualKey, b.Modifiers);
            if (byCombo.TryGetValue(key, out var firstOwner))
            {
                result[b.CommandId] = new ConflictInfo(firstOwner);
                if (!result.ContainsKey(firstOwner))
                    result[firstOwner] = new ConflictInfo(b.CommandId);
            }
            else
            {
                byCombo[key] = b.CommandId;
            }
        }
        return result;
    }

    public static ConflictInfo? CheckProposed(
        IEnumerable<HotkeyBinding> existing,
        uint vk,
        HotkeyModifiers mods,
        string? excludeCommandId)
    {
        foreach (var b in existing)
        {
            if (excludeCommandId is not null && b.CommandId == excludeCommandId) continue;
            if (b.VirtualKey == vk && b.Modifiers == mods) return new ConflictInfo(b.CommandId);
        }
        return null;
    }
}
