using System.Text.Json;
using System.Windows;
using Legolas.Domain;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Sharing;
using Mithril.Shared.Wpf.Dialogs;

namespace Legolas.Sharing;

/// <summary>
/// Legolas's implementation of <see cref="ILegolasShareImportTarget"/>. Decodes the
/// deep link's base64url payload, deserializes a <see cref="LegolasSharePayload"/>,
/// and opens the same <see cref="LegolasShareDialog"/> the sender used — giving the
/// receiver the full toolkit (text summary, JSON, card preview, copy/save buttons,
/// re-share link) instead of a stripped-down read-only view.
/// </summary>
public sealed class LegolasShareImportTarget : ILegolasShareImportTarget
{
    private readonly LegolasShareCardRenderer? _renderer;
    private readonly LegolasSettings? _settings;
    private readonly IDialogService? _dialogs;
    private readonly IReferenceDataService? _refData;
    private readonly IModuleActivator? _activator;
    private readonly IDiagnosticsSink? _diag;

    public LegolasShareImportTarget(
        LegolasShareCardRenderer? renderer = null,
        LegolasSettings? settings = null,
        IDialogService? dialogs = null,
        IReferenceDataService? refData = null,
        IModuleActivator? activator = null,
        IDiagnosticsSink? diag = null)
    {
        _renderer = renderer;
        _settings = settings;
        _dialogs = dialogs;
        _refData = refData;
        _activator = activator;
        _diag = diag;
    }

    public void ImportFromLinkPayload(string base64UrlPayload)
    {
        if (!ShareCodec.TryDecodePayload(base64UrlPayload, out var json, out var error))
        {
            _diag?.Info("Legolas", $"Share link decode failed: {error}");
            return;
        }

        LegolasSharePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, LegolasShareJsonContext.Default.LegolasSharePayload);
        }
        catch (JsonException ex)
        {
            _diag?.Info("Legolas", $"Share link payload is not valid LegolasSharePayload JSON: {ex.Message}");
            return;
        }
        if (payload is null)
        {
            _diag?.Info("Legolas", "Share link payload deserialized to null.");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Show(payload);
        else
            dispatcher.InvokeAsync(() => Show(payload));
    }

    private void Show(LegolasSharePayload payload)
    {
        if (_activator is not null && !_activator.Activate("legolas"))
            _diag?.Info("Legolas", "Deep-link import: module activator could not find 'legolas'.");

        if (_dialogs is null || _settings is null)
        {
            _diag?.Info("Legolas", "Share import: no dialog service or settings registered; cannot open dialog.");
            return;
        }

        // The receiver doesn't get to anonymize someone else's identity — the
        // toggle is hidden via showCharacterNameToggle=false, and the buildPayload
        // ignores the bool and always returns what the sender encoded.
        var hadName = !string.IsNullOrWhiteSpace(payload.CharacterName);
        var vm = new LegolasShareDialogViewModel(
            buildPayload: _ => payload,
            renderer: _renderer,
            settings: _settings,
            hasCharacterName: hadName,
            showCharacterNameToggle: false,
            refData: _refData);
        _dialogs.ShowDialog(vm, new LegolasShareDialog());
    }
}
