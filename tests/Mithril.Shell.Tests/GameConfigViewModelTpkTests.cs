using FluentAssertions;
using Mithril.Shared.Game;
using Mithril.Shared.MapCalibration;
using Mithril.Shell.ViewModels;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #960: the Game-Configuration download button wiring — the VM reflects the
/// provisioner's installed state, runs the download command, and updates its
/// status + enabled-state from the result without ever throwing.
/// </summary>
public sealed class GameConfigViewModelTpkTests
{
    [Fact]
    public void Reflects_installed_state_at_construction()
    {
        var vm = new GameConfigViewModel(new GameConfig(), new FakeProvisioner { Installed = true });

        vm.TpkInstalled.Should().BeTrue();
        vm.CanDownloadTpk.Should().BeFalse("an already-installed tpk needs no download");
    }

    [Fact]
    public void Offers_download_when_not_installed()
    {
        var vm = new GameConfigViewModel(new GameConfig(), new FakeProvisioner { Installed = false });

        vm.TpkInstalled.Should().BeFalse();
        vm.CanDownloadTpk.Should().BeTrue();
    }

    [Fact]
    public void Cannot_download_without_a_provisioner()
    {
        var vm = new GameConfigViewModel(new GameConfig(), tpkProvisioner: null);

        vm.CanDownloadTpk.Should().BeFalse();
    }

    [Fact]
    public async Task Successful_download_flips_installed_and_updates_status()
    {
        var prov = new FakeProvisioner
        {
            Installed = false,
            Result = new TpkProvisionResult(TpkProvisionStatus.Downloaded, "Downloaded and verified."),
            InstalledAfterEnsure = true,
        };
        var vm = new GameConfigViewModel(new GameConfig(), prov);

        await vm.DownloadTpkCommand.ExecuteAsync(null);

        vm.TpkInstalled.Should().BeTrue();
        vm.CanDownloadTpk.Should().BeFalse();
        vm.DownloadTpkStatus.Should().Contain("installed");
    }

    [Fact]
    public async Task Failed_download_leaves_not_installed_and_surfaces_message()
    {
        var prov = new FakeProvisioner
        {
            Installed = false,
            Result = new TpkProvisionResult(TpkProvisionStatus.Failed, "Download failed: network down"),
            InstalledAfterEnsure = false,
        };
        var vm = new GameConfigViewModel(new GameConfig(), prov);

        await vm.DownloadTpkCommand.ExecuteAsync(null);

        vm.TpkInstalled.Should().BeFalse();
        vm.CanDownloadTpk.Should().BeTrue();
        vm.DownloadTpkStatus.Should().Contain("failed");
    }

    private sealed class FakeProvisioner : IClassDataTpkProvisioner
    {
        public bool Installed { get; set; }
        public bool InstalledAfterEnsure { get; set; }
        public TpkProvisionResult Result { get; set; } =
            new(TpkProvisionStatus.AlreadyPresent, "Already installed.");

        private bool _ensured;

        public bool IsInstalled() => _ensured ? InstalledAfterEnsure : Installed;

        public Task<TpkProvisionResult> EnsureAsync(
            IProgress<TpkProvisionProgress>? progress, CancellationToken ct)
        {
            _ensured = true;
            return Task.FromResult(Result);
        }
    }
}
