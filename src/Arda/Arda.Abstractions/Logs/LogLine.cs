namespace Arda.Abstractions.Logs;

/// <summary>
/// The public output of the L0/L1 log ingest pipeline: a single game-event
/// line with its timestamp prefix stripped and metadata attached.
/// <para>
/// <see cref="Log"/> contains the text after the timestamp prefix (e.g.
/// <c>LocalPlayer: ProcessAddPlayer(...)</c>). <see cref="Raw"/> optionally
/// retains the original line including the prefix for diagnostic purposes.
/// </para>
/// </summary>
public sealed record LogLine(
    string Log,
    LogLineMetadata Metadata,
    string? Raw = null);
