using System;
using System.Security.Cryptography;
using System.Text;

namespace Mithril.Shared.Telemetry.Settings;

/// <summary>
/// DPAPI wrap/unwrap with a recognisable prefix (<see cref="WrapPrefix"/>) so
/// <see cref="Unprotect"/> can tell a wrapped value from a plaintext one and
/// pass plaintext through unchanged. Required for graceful first-load: a
/// telemetry.json hand-edited or imported plaintext shouldn't error out.
///
/// Scope: <see cref="DataProtectionScope.CurrentUser"/>. Wrapped values are
/// only unwrappable by the same Windows user account that wrote them.
/// </summary>
public sealed class HeaderValueProtection
{
    private const string WrapPrefix = "dpapi:";

    public string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var bytes = Encoding.UTF8.GetBytes(value);
        var wrapped = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return WrapPrefix + Convert.ToBase64String(wrapped);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(WrapPrefix, StringComparison.Ordinal)) return value;
        var blob = Convert.FromBase64String(value.AsSpan(WrapPrefix.Length).ToString());
        var unwrapped = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(unwrapped);
    }
}
