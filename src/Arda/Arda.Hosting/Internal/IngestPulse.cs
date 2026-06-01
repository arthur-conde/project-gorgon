using Arda.Abstractions.Diagnostics;

namespace Arda.Hosting.Internal;

/// <summary>
/// Default <see cref="IIngestPulse"/> + <see cref="IIngestPulseSink"/>
/// implementation. Shared singleton — Arda.Ingest writes via the sink,
/// <c>WorldHealthView</c> reads via the pulse and subscribes to
/// <see cref="Pulsed"/> for stall detection.
/// <para>
/// Per-family <c>LastPoll</c> reads and writes are serialized through a single
/// <c>lock</c> so the read-side snapshot is internally consistent; the lock is
/// uncontended in practice (one writer per family, occasional reader). The
/// event fires synchronously from the writer's thread (the ingest poll loop),
/// so subscribers must avoid blocking work in the handler.
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

    public void RecordPoll(LogFamily family, DateTimeOffset polledAt, int linesEmitted)
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

        Pulsed?.Invoke(this, new IngestPulseEventArgs(family, polledAt, linesEmitted));
    }
}
