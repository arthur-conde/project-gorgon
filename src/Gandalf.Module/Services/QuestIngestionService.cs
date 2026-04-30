using Gandalf.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Gandalf.Services;

/// <summary>
/// Subscribes to <see cref="IPlayerLogStream"/> and routes quest journal events
/// (<c>ProcessLoadQuests</c> bulk login + <c>ProcessBook("New Quest:" …)</c>
/// per-accept + <c>ProcessCompleteQuest</c>) into <see cref="QuestSource"/>.
/// No <c>ModuleGate</c> wait — Gandalf is eager.
/// </summary>
public sealed class QuestIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly QuestJournalLoadParser _journalLoad;
    private readonly QuestAcceptedParser _accepted;
    private readonly QuestCompletedParser _completed;
    private readonly QuestSource _source;
    private readonly IDiagnosticsSink? _diag;
    private bool _firstObservationLogged;

    public QuestIngestionService(
        IPlayerLogStream stream,
        QuestJournalLoadParser journalLoad,
        QuestAcceptedParser accepted,
        QuestCompletedParser completed,
        QuestSource source,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _journalLoad = journalLoad;
        _accepted = accepted;
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
        if (_journalLoad.TryParse(raw.Line, raw.Timestamp) is QuestJournalLoadedEvent loaded)
        {
            _source.OnQuestJournalLoaded(loaded.WorkOrderQuestIds, loaded.RegularQuestIds);
            FirstObservation();
            return;
        }

        if (_accepted.TryParse(raw.Line, raw.Timestamp) is QuestAcceptedEvent accepted)
        {
            _source.OnQuestAccepted(accepted.QuestInternalName);
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
