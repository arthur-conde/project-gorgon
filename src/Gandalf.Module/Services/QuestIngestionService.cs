using Gandalf.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// Subscribes to <see cref="IPlayerLogStream"/> and routes quest journal events
/// (<c>ProcessLoadQuest</c> / <c>ProcessCompleteQuest</c>) into
/// <see cref="QuestSource"/>. No <c>ModuleGate</c> wait — Gandalf is eager.
/// </summary>
public sealed class QuestIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly QuestLoadedParser _loaded;
    private readonly QuestCompletedParser _completed;
    private readonly QuestSource _source;
    private readonly IDiagnosticsSink? _diag;
    private bool _firstObservationLogged;

    public QuestIngestionService(
        IPlayerLogStream stream,
        QuestLoadedParser loaded,
        QuestCompletedParser completed,
        QuestSource source,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _loaded = loaded;
        _completed = completed;
        _source = source;
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try { Dispatch(raw); }
            catch (Exception ex) { _diag?.Warn("Gandalf.Quest", $"Ingestion error: {ex.Message}"); }
        }
    }

    private void Dispatch(RawLogLine raw)
    {
        if (_loaded.TryParse(raw.Line, raw.Timestamp) is QuestLoadedEvent loaded)
        {
            _source.OnQuestLoaded(loaded.QuestInternalName);
            FirstObservation();
            return;
        }

        if (_completed.TryParse(raw.Line, raw.Timestamp) is QuestCompletedEvent completed)
        {
            _source.OnQuestCompleted(completed.QuestInternalName, completed.Timestamp);
            FirstObservation();
        }
    }

    private void FirstObservation()
    {
        if (_firstObservationLogged) return;
        _firstObservationLogged = true;
        _diag?.Info("Gandalf.Quest", "First quest-source event observed this session");
    }
}
