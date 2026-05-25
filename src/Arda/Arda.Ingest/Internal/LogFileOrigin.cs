namespace Arda.Ingest.Internal;

/// <summary>
/// Identifies the logical origin file within a log family. Internal to the
/// ingest layer — no consumer outside Arda.Ingest sees this. Used by source
/// coordinators to track which physical file a line came from when computing
/// combined byte-offset positions for resumption.
/// </summary>
internal enum LogFileOrigin
{
    /// <summary>The historic log from the previous game launch session (Player-prev.log).</summary>
    PlayerPrev,

    /// <summary>The current live log file for this game session (Player.log).</summary>
    Player,

    /// <summary>A date-rotated chat log slice (Chat-yy-mm-dd.log).</summary>
    Chat
}
