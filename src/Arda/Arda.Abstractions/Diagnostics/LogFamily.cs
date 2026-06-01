namespace Arda.Abstractions.Diagnostics;

/// <summary>
/// Identifies which log family a tailer-poll pulse came from.
/// <para>
/// Placed in <c>Arda.Abstractions</c> so the producer (Arda.Ingest, which
/// records pulses via <see cref="IIngestPulseSink"/>) and the consumer
/// (Arda.Hosting's <c>IIngestPulse</c>) can share the vocabulary without
/// either project taking a hard reference on the other. Arda.Hosting owns
/// the read-side interface because that's where the singleton is composed;
/// Arda.Ingest only sees the write-side sink.
/// </para>
/// </summary>
public enum LogFamily
{
    /// <summary>The Player.log family (Player.log + Player-prev.log).</summary>
    Player,

    /// <summary>The chat family (ChatLogs/Chat-yy-mm-dd.log).</summary>
    Chat
}
