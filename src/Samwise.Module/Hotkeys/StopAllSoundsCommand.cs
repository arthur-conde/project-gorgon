using Mithril.Shared.Audio;
using Mithril.Shared.Hotkeys;

namespace Samwise.Hotkeys;

public sealed class StopAllSoundsCommand : IHotkeyCommand
{
    public string Id => "mithril.stop-all-sounds";
    public string DisplayName => "Stop all alarm sounds";
    public string? Category => "Audio";
    public HotkeyBinding? DefaultBinding => null;

    public Task ExecuteAsync(CancellationToken ct)
    {
        AudioPlayer.Stop();
        return Task.CompletedTask;
    }
}
