namespace Arda.Hosting;

/// <summary>
/// Configuration for the Arda pipeline. Passed to
/// <see cref="ArdaServiceCollectionExtensions.AddArda"/>.
/// </summary>
public sealed record ArdaOptions(
    /// <summary>
    /// Directory containing Player.log / Player-prev.log.
    /// Typically <c>%LocalAppData%Low/Elder Game/Project Gorgon/</c>.
    /// </summary>
    string LogDirectory,

    /// <summary>
    /// Directory containing dated chat logs (Chat-yy-MM-dd.log).
    /// Defaults to <c>LogDirectory/ChatLogs/</c> if null.
    /// </summary>
    string? ChatLogDirectory = null,

    /// <summary>
    /// Polling interval for the file tailer. Defaults to 250ms.
    /// </summary>
    TimeSpan? PollInterval = null);
