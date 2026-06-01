using Arda.Abstractions.Diagnostics;

namespace Arda.Hosting.Internal;

/// <summary>
/// Default <see cref="IIngestPulse"/> + <see cref="IIngestPulseSink"/>
/// implementation. Shared singleton — Arda.Ingest writes via the sink,
/// <c>WorldHealthView</c> reads via the pulse and subscribes to
/// <see cref="Pulsed"/> for stall detection.
/// <para>
/// Per-family <c>LastPoll</c> is stored in <see cref="Volatile.Read"/> /
/// <see cref="Volatile.Write"/>-style fields; reads are lock-free. The event
/// fires synchronously from the writer's thread (the ingest poll loop), so
/// subscribers must avoid blocking work in the handler.
/// </para>
/// </summary>
internal sealed class IngestPulse : IIngestPulse, IIngestPulseSink
{
    private readonly object _gate = new();
    private DateTimeOffset? _playerLastPoll;
    private DateTimeOffset? _chatLastPoll;

    public DateTimeOffset? LastPoll(LogFamily family)
    {
        lock (_gate)
        {
            return family switch
            {
                LogFamily.Player => _playerLastPoll,
                LogFamily.Chat => _chatLastPoll,
                _ => null,
            };
        }
    }

    public event EventHandler<IngestPulseEventArgs>? Pulsed;

    public void RecordPoll(LogFamily family, DateTimeOffset polledAt, int bytesRead, int linesEmitted)
    {
        lock (_gate)
        {
            switch (family)
            {
                case LogFamily.Player:
                    _playerLastPoll = polledAt;
                    break;
                case LogFamily.Chat:
                    _chatLastPoll = polledAt;
                    break;
            }
        }

        Pulsed?.Invoke(this, new IngestPulseEventArgs(family, polledAt, bytesRead, linesEmitted));
    }
}
