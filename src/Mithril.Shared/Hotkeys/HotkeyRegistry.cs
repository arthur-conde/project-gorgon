namespace Mithril.Shared.Hotkeys;

public sealed class HotkeyRegistry
{
    private readonly Dictionary<string, IHotkeyCommand> _commands;

    public HotkeyRegistry(IEnumerable<IHotkeyCommand> commands)
    {
        _commands = commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IHotkeyCommand> Commands => _commands.Values;

    public bool TryGet(string id, out IHotkeyCommand command) => _commands.TryGetValue(id, out command!);
}
