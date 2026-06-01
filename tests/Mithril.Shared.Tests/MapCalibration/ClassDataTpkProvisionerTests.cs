using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FluentAssertions;
using Mithril.Shared.MapCalibration;
using Xunit;

namespace Mithril.Shared.Tests.MapCalibration;

/// <summary>
/// #960: the BCL-only <see cref="ClassDataTpkProvisioner"/> over a fake
/// <see cref="HttpMessageHandler"/> — happy path, idempotency, and the fail-closed
/// verify paths (size floor / SHA mismatch / network throw), each asserting the
/// canonical file is NOT placed and the temp file is cleaned up. Never throws.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ClassDataTpkProvisionerTests : IDisposable
{
    private readonly string _cacheDir;

    public ClassDataTpkProvisionerTests()
    {
        _cacheDir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-tpk-tests");
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private string TpkPath => Path.Combine(_cacheDir, ClassDataTpkProvisioner.TpkFileName);

    // The provisioner pins ExpectedSha256 to the real ~283 KB upstream file, which
    // we can't reproduce byte-for-byte in a unit test. The internal test-seam ctor
    // (InternalsVisibleTo) lets the happy-path test supply a controllable expected
    // SHA + size envelope so we can drive download→verify→place over a fake handler;
    // the fail-closed tests use the production public ctor (real pinned SHA), so an
    // arbitrary buffer correctly fails the SHA check.
    private static byte[] PaddedBuffer(int size, byte fill)
    {
        var buf = new byte[size];
        Array.Fill(buf, fill);
        return buf;
    }

    private static HttpClient HttpReturning(byte[] body) =>
        new(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        }));

    private static HttpClient HttpThrowing() =>
        new(new ThrowingHandler("network down"));

    [Fact]
    public async Task AlreadyPresent_skips_network_when_valid_tpk_exists()
    {
        // A file at the canonical path, above the size floor → IsInstalled() true.
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(TpkPath, PaddedBuffer(250_000, 0x42));

        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpThrowing());

        sut.IsInstalled().Should().BeTrue();

        var result = await sut.EnsureAsync(progress: null, CancellationToken.None);

        result.Status.Should().Be(TpkProvisionStatus.AlreadyPresent);
        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Happy_path_downloads_verifies_and_places_the_file()
    {
        // Drive the placement path through the internal test seam: a buffer above
        // the floor, with the expected SHA set to the buffer's own hash.
        var body = PaddedBuffer(250_000, 0x17);
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));

        var sut = new ClassDataTpkProvisioner(
            _cacheDir, HttpReturning(body),
            expectedSha: sha, minSizeBytes: 200_000, maxSizeBytes: 5_000_000);

        var result = await sut.EnsureAsync(progress: null, CancellationToken.None);

        result.Status.Should().Be(TpkProvisionStatus.Downloaded);
        File.Exists(TpkPath).Should().BeTrue();
        File.ReadAllBytes(TpkPath).Should().Equal(body);
        NoTempLeftBehind();
    }

    [Fact]
    public async Task Size_below_floor_is_rejected_and_no_file_placed()
    {
        var body = PaddedBuffer(1_000, 0x01); // well under the 200 KB floor
        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpReturning(body));

        var result = await sut.EnsureAsync(progress: null, CancellationToken.None);

        result.Status.Should().Be(TpkProvisionStatus.Failed);
        File.Exists(TpkPath).Should().BeFalse();
        NoTempLeftBehind();
    }

    [Fact]
    public async Task Sha_mismatch_is_rejected_and_no_file_placed()
    {
        // Right size, wrong hash (the pinned SHA won't match this arbitrary buffer).
        var body = PaddedBuffer(250_000, 0x55);
        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpReturning(body));

        var result = await sut.EnsureAsync(progress: null, CancellationToken.None);

        result.Status.Should().Be(TpkProvisionStatus.Failed);
        result.Message.Should().Contain("integrity");
        File.Exists(TpkPath).Should().BeFalse();
        NoTempLeftBehind();
    }

    [Fact]
    public async Task Network_exception_returns_Failed_without_throwing()
    {
        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpThrowing());

        var result = await sut.EnsureAsync(progress: null, CancellationToken.None);

        result.Status.Should().Be(TpkProvisionStatus.Failed);
        File.Exists(TpkPath).Should().BeFalse();
        NoTempLeftBehind();
    }

    [Fact]
    public void IsInstalled_false_for_undersized_file()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(TpkPath, PaddedBuffer(1_000, 0x09)); // under the floor

        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpThrowing());

        sut.IsInstalled().Should().BeFalse();
    }

    [Fact]
    public void IsInstalled_false_when_absent()
    {
        var sut = new ClassDataTpkProvisioner(_cacheDir, HttpThrowing());
        sut.IsInstalled().Should().BeFalse();
    }

    private void NoTempLeftBehind()
    {
        if (!Directory.Exists(_cacheDir)) return;
        Directory.GetFiles(_cacheDir, "*.partial").Should().BeEmpty();
        Directory.GetFiles(_cacheDir, "*.download-*").Should().BeEmpty();
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException(message);
    }
}
