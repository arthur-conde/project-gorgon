using System.Windows;
using Celebrimbor.ViewModels;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Celebrimbor.Services;

/// <summary>
/// Celebrimbor's implementation of <see cref="ICraftListImportTarget"/>. Handles incoming
/// <c>mithril://list/…</c> deep links by: bringing the Celebrimbor tab to the foreground,
/// decoding the base64url payload into a <see cref="ParseResult"/>, then running the shared
/// Append/Replace/Cancel dialog on the UI thread via <see cref="RecipePickerViewModel"/>.
/// </summary>
public sealed class CraftListImportTarget : ICraftListImportTarget
{
    private readonly RecipePickerViewModel _picker;
    private readonly IReferenceDataService _refData;
    private readonly IModuleActivator? _activator;
    private readonly IDiagnosticsSink? _diag;

    public CraftListImportTarget(
        RecipePickerViewModel picker,
        IReferenceDataService refData,
        IModuleActivator? activator = null,
        IDiagnosticsSink? diag = null)
    {
        _picker = picker;
        _refData = refData;
        _activator = activator;
        _diag = diag;
    }

    public void ImportFromLinkPayload(string base64UrlPayload)
    {
        var result = CraftListFormat.DecodeShareLink(base64UrlPayload, _refData);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply(result);
        else
            dispatcher.InvokeAsync(() => Apply(result));
    }

    private void Apply(ParseResult result)
    {
        // Bring the Celebrimbor tab forward first so the dialog owner is the right window and
        // the user sees the list populate after confirming.
        if (_activator is not null && !_activator.Activate("celebrimbor"))
            _diag?.Info("Celebrimbor", "Deep-link import: module activator could not find 'celebrimbor'.");

        _picker.PromptAndApply(result, "Import from link");
    }
}
