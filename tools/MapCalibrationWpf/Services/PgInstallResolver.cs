namespace Mithril.Tools.MapCalibrationWpf.Services;

using System.IO;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Settings;

/// <summary>
/// Resolves the Project Gorgon install path for the in-process asset extractors.
/// Tries (1) the persisted user override (typed in via
/// <c>PgInstallPickerDialog</c>) and (2) <see cref="SteamInstall.FindPgInstall"/>
/// — the same auto-detect path the CLI uses. Returns null when neither yields
/// an existing directory, so callers can prompt the user to point at it once.
///
/// <para><see cref="SteamInstall.FindPgInstall"/> throws <c>UserFacingException</c>
/// when it can't locate PG; we trap that here and convert to a nullable so the
/// dialog flow can react to "no install found" the same way as "user hasn't
/// configured one yet".</para>
/// </summary>
public sealed class PgInstallResolver
{
    public string? Resolve()
    {
        var persisted = PersistedSettings.Load();
        if (!string.IsNullOrEmpty(persisted.PgInstallPathOverride)
            && Directory.Exists(persisted.PgInstallPathOverride))
        {
            return persisted.PgInstallPathOverride;
        }
        try
        {
            return SteamInstall.FindPgInstall();
        }
        catch (UserFacingException)
        {
            return null;
        }
    }

    public void PersistOverride(string path)
    {
        new PersistedSettings(path).Save();
    }
}
