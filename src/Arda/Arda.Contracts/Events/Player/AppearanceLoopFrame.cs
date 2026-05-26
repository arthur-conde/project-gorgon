using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Fired when a <c>Download appearance loop @Model(scale=N)</c> line is observed.
/// Not verb-dispatched — emitted by an <see cref="Arda.Dispatch.ILineObserver"/>
/// that regex-matches the raw line. Primary consumer: Samwise (plant model detection).
/// </summary>
public readonly record struct AppearanceLoopFrame(
    ReadOnlyMemory<char> ModelName,
    double Scale,
    LogLineMetadata Metadata);
