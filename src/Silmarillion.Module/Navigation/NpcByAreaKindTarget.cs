using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.NpcByArea"/>. Flips to the
/// NPCs tab and pre-populates <see cref="NpcsTabViewModel.QueryText"/> with
/// <c>AreaName = "&lt;areaKey&gt;"</c>. Exact-match — friendly names overlap (e.g. typing
/// <c>"Serbule"</c> matches <c>"Serbule Hills"</c> and <c>"Caves of Serbule"</c>), but the
/// envelope key is unique. Powers the Areas-tab "NPCs in this area" overflow pill.
/// </summary>
public sealed class NpcByAreaKindTarget : IReferenceKindTarget
{
    private readonly NpcsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public NpcByAreaKindTarget(NpcsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.NpcByArea;

    public int TabIndex => 2; // NPCs tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _diag?.Info("Silmarillion.Nav", "NpcByArea.TrySelect '' → empty payload, leaving NPCs tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"AreaName = \"{escaped}\"";
        _diag?.Info("Silmarillion.Nav", $"NpcByArea.TrySelect '{internalName}' → QueryText='{query}'.");
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
