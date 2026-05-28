using FluentAssertions;
using Mithril.Shared.Telemetry.Settings;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Settings;

public class HeaderValueProtectionTests
{
    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var p = new HeaderValueProtection();
        var wrapped = p.Protect("secret-api-key");
        wrapped.Should().NotBe("secret-api-key");
        p.Unprotect(wrapped).Should().Be("secret-api-key");
    }

    [Fact]
    public void Protect_null_or_empty_returns_input_unchanged()
    {
        var p = new HeaderValueProtection();
        p.Protect(null).Should().BeNull();
        p.Protect("").Should().Be("");
    }

    [Fact]
    public void Unprotect_returns_input_unchanged_when_not_a_dpapi_blob()
    {
        // Backward-compat path: existing plaintext header survives a load+save cycle
        // until next save naturally re-wraps it. Avoids data loss if user hand-edits.
        var p = new HeaderValueProtection();
        p.Unprotect("not-wrapped").Should().Be("not-wrapped");
    }

    [Fact]
    public void Unprotect_returns_null_when_dpapi_blob_is_corrupted()
    {
        var p = new HeaderValueProtection();
        p.Unprotect("dpapi:not-valid-base64!!!").Should().BeNull();
        p.Unprotect("dpapi:Zm9vYmFy").Should().BeNull(); // valid base64 but not a real DPAPI blob
    }
}
