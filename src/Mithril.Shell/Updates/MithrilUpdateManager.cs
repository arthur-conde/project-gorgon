using Velopack;
using Velopack.Sources;

namespace Mithril.Shell.Updates;

/// <summary>
/// Singleton wrapper around Velopack's <see cref="UpdateManager"/> that also caches the
/// most recent <see cref="UpdateInfo"/> across services. The checker writes
/// <see cref="Pending"/>; the applier consumes it.
/// </summary>
/// <remarks>
/// In <see cref="UpdateChannelInfo.IsDevelopment"/> builds the inner manager is null and
/// <see cref="IsAvailable"/> returns false — both the checker and applier short-circuit
/// so F5 sessions never touch the network or Velopack staging directory.
/// </remarks>
public sealed class MithrilUpdateManager
{
    public const string RepoUrl = "https://github.com/arthur-conde/project-gorgon";

    private readonly UpdateManager? _manager;

    public MithrilUpdateManager(UpdateChannelInfo channel)
    {
        Channel = channel;
        if (channel.IsDevelopment) return;

        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = channel.Name });
    }

    public UpdateChannelInfo Channel { get; }

    public UpdateManager Manager => _manager
        ?? throw new InvalidOperationException("UpdateManager unavailable in development channel.");

    public bool IsAvailable => _manager is not null;

    /// <summary>True when running from a Velopack-installed location (Setup.exe path).
    /// False for portable ZIP extracts and dev builds. <see cref="VelopackUpdateApplier"/>
    /// uses this to decide whether to call <c>ApplyUpdatesAndRestart</c> or fall back to
    /// opening the Releases page in the user's browser.</summary>
    public bool IsInstalled => _manager?.IsInstalled ?? false;

    /// <summary>Last <c>CheckForUpdatesAsync</c> result. Null when up to date or when no
    /// check has run yet.</summary>
    public UpdateInfo? Pending { get; set; }
}
