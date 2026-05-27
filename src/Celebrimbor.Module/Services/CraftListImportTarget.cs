using Microsoft.Extensions.Logging;
using System.Windows;
using Celebrimbor.Domain;
using Celebrimbor.ViewModels;
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
    private readonly ILogger? _logger;

    public CraftListImportTarget(
        RecipePickerViewModel picker,
        IReferenceDataService refData,
        IModuleActivator? activator = null,
        ILogger? logger = null)
    {
        _picker = picker;
        _refData = refData;
        _activator = activator;
        _logger = logger;
    }

    public void ImportFromLinkPayload(string base64UrlPayload)
    {
        var result = CraftListFormat.DecodeShareLink(base64UrlPayload, _refData);
        Dispatch(() => Apply(result, "Import from link"));
    }

    public void ImportRecipes(IReadOnlyList<CraftListImportEntry> recipes, string source)
    {
        var entries = new List<CraftListEntry>();
        var warnings = new List<string>();
        foreach (var r in recipes)
        {
            if (string.IsNullOrWhiteSpace(r.RecipeInternalName) || r.Quantity <= 0) continue;
            if (!_refData.RecipesByInternalName.ContainsKey(r.RecipeInternalName))
            {
                warnings.Add($"Unknown recipe: \"{r.RecipeInternalName}\"");
                continue;
            }
            entries.Add(new CraftListEntry { RecipeInternalName = r.RecipeInternalName, Quantity = r.Quantity });
        }

        var result = new ParseResult(entries, warnings);
        Dispatch(() => Apply(result, source));
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }

    private void Apply(ParseResult result, string dialogTitleSuffix)
    {
        // Bring the Celebrimbor tab forward first so the dialog owner is the right window and
        // the user sees the list populate after confirming.
        if (_activator is not null && !_activator.Activate("celebrimbor"))
            _logger?.LogInformation("Craft-list import: module activator could not find 'celebrimbor'.");

        _picker.PromptAndApply(result, dialogTitleSuffix);
    }
}
