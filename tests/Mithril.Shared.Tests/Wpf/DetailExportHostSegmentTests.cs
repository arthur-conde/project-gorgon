using System;
using System.Threading;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Locks the null/empty-filter projection contract of
/// <c>DetailExportHost.OnFooterSegmentsChanged</c>. <c>DetailExportHost</c> is a
/// <c>ContentControl</c> and requires an STA thread; we follow the same pattern as
/// <c>QueryFilterTests</c> — each test runs inside a short-lived STA thread.
/// </summary>
public sealed class DetailExportHostSegmentTests
{
    [Fact]
    public void FooterSegments_NullAndEmpty_AreFiltered_ValidSegmentsProjected()
    {
        RunOnSta(() =>
        {
            var host = new DetailExportHost();
            host.FooterSegments = ["A", null!, "", "B"];

            host.FooterSegmentItems.Should().HaveCount(2);
            host.FooterSegmentItems[0].Text.Should().Be("A");
            host.FooterSegmentItems[0].IsFirst.Should().BeTrue();
            host.FooterSegmentItems[1].Text.Should().Be("B");
            host.FooterSegmentItems[1].IsFirst.Should().BeFalse();
            host.HasFooterSegments.Should().BeTrue();
        });
    }

    [Fact]
    public void FooterSegments_NullInput_ProducesEmptyItemsAndFalseFlag()
    {
        RunOnSta(() =>
        {
            var host = new DetailExportHost();
            host.FooterSegments = null;

            host.FooterSegmentItems.Should().BeEmpty();
            host.HasFooterSegments.Should().BeFalse();
        });
    }

    [Fact]
    public void FooterSegments_AllNullOrEmpty_ProducesEmptyItemsAndFalseFlag()
    {
        RunOnSta(() =>
        {
            var host = new DetailExportHost();
            host.FooterSegments = [null!, "", null!];

            host.FooterSegmentItems.Should().BeEmpty();
            host.HasFooterSegments.Should().BeFalse();
        });
    }

    [Fact]
    public void FooterSegments_WhitespaceOnly_IsNotFiltered()
    {
        // The filter is !string.IsNullOrEmpty, so whitespace-only strings pass through.
        RunOnSta(() =>
        {
            var host = new DetailExportHost();
            host.FooterSegments = [" "];

            host.FooterSegmentItems.Should().ContainSingle()
                .Which.Text.Should().Be(" ");
            host.HasFooterSegments.Should().BeTrue();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}
