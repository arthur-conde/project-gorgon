using Arda.Abstractions.Logs;

namespace Arda.Dispatch;

/// <summary>
/// Handles a dispatched verb from the L2 driver. Receives the args span
/// (everything after the verb in the log line) and the line metadata.
/// The handler tokenizes positionally on the span and emits domain events.
/// <para>
/// Implementations should avoid allocating strings for values they don't
/// need to persist. Use <see cref="ArgTokenizer"/> for positional parsing
/// and <see cref="InternPool"/> for known identifier families.
/// </para>
/// </summary>
public interface IFrameHandler
{
    /// <summary>
    /// Process a dispatched line. <paramref name="args"/> contains everything
    /// after the verb (including the opening parenthesis for Process* verbs).
    /// <paramref name="sourceLog"/> is the full <see cref="LogLine.Log"/> string,
    /// available for <see cref="ReadOnlyMemory{T}"/> slicing in passthrough handlers.
    /// </summary>
    void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata);
}
