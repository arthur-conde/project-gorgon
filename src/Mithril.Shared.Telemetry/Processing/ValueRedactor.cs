namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// Substring redactor applied to every string tag value AFTER the allowlist
/// filter accepts the tag key. Defence-in-depth: even an enabled tag whose
/// value accidentally contains the active character name or a user-bearing
/// path is sanitised before export.
///
/// Ordering matters &mdash; longer prefix (<c>localAppData</c>) is replaced
/// before <c>userProfile</c> so <c>C:\Users\alice\AppData\Local\&#8230;</c>
/// becomes <c>$LOCALAPPDATA\&#8230;</c> rather than <c>$USER\AppData\Local\&#8230;</c>.
/// </summary>
public sealed class ValueRedactor
{
    private readonly Func<string?> _getActiveCharacter;
    private readonly string _userProfile;
    private readonly string _localAppData;

    /// <summary>
    /// Creates a redactor that scrubs the supplied user-profile and
    /// local-app-data path prefixes, plus the active character name returned
    /// by <paramref name="getActiveCharacter"/> on each call.
    /// </summary>
    /// <param name="getActiveCharacter">
    /// Callback returning the currently active character name, or <c>null</c>
    /// when no character is logged in. Re-invoked per <see cref="Redact"/>
    /// call so character switches are picked up without rebuilding the
    /// redactor.
    /// </param>
    /// <param name="userProfile">
    /// Absolute path to the user's profile directory (e.g.
    /// <c>C:\Users\alice</c>) to replace with the <c>$USER</c> token.
    /// </param>
    /// <param name="localAppData">
    /// Absolute path to the user's local-app-data directory (e.g.
    /// <c>C:\Users\alice\AppData\Local</c>) to replace with the
    /// <c>$LOCALAPPDATA</c> token. Replaced before <paramref name="userProfile"/>
    /// because it is the longer prefix.
    /// </param>
    public ValueRedactor(Func<string?> getActiveCharacter, string userProfile, string localAppData)
    {
        _getActiveCharacter = getActiveCharacter;
        _userProfile = userProfile;
        _localAppData = localAppData;
    }

    /// <summary>
    /// Returns <paramref name="value"/> with known PII substrings replaced by
    /// stable tokens. <c>null</c> and empty input are returned unchanged.
    /// </summary>
    public string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Path prefixes first (longest first).
        if (!string.IsNullOrEmpty(_localAppData))
            value = value.Replace(_localAppData, "$LOCALAPPDATA", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_userProfile))
            value = value.Replace(_userProfile, "$USER", StringComparison.OrdinalIgnoreCase);

        // Character name - read on every call so it tracks character switches.
        var character = _getActiveCharacter();
        if (!string.IsNullOrEmpty(character))
            value = value.Replace(character, "$CHARACTER", StringComparison.OrdinalIgnoreCase);

        return value;
    }
}
