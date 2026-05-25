using System.ComponentModel;
using Arda.Hosting.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.Hosting.Tests;

public class ReplayProgressTests
{
    [Fact]
    public void Initially_ProgressIsZero()
    {
        var progress = new ReplayProgress();

        progress.PlayerProgress.Should().Be(0.0);
        progress.ChatProgress.Should().Be(0.0);
    }

    [Fact]
    public void Initially_ReplayCompleteIsNotCompleted()
    {
        var progress = new ReplayProgress();

        progress.ReplayComplete.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void MarkComplete_Player_SetsProgressToOne()
    {
        var progress = new ReplayProgress();

        progress.MarkComplete(ReplayProgress.SourceFamily.Player);

        progress.PlayerProgress.Should().Be(1.0);
    }

    [Fact]
    public void MarkComplete_Chat_SetsProgressToOne()
    {
        var progress = new ReplayProgress();

        progress.MarkComplete(ReplayProgress.SourceFamily.Chat);

        progress.ChatProgress.Should().Be(1.0);
    }

    [Fact]
    public void MarkComplete_BothFamilies_CompletesReplayTask()
    {
        var progress = new ReplayProgress();

        progress.MarkComplete(ReplayProgress.SourceFamily.Player);
        progress.ReplayComplete.IsCompleted.Should().BeFalse();

        progress.MarkComplete(ReplayProgress.SourceFamily.Chat);
        progress.ReplayComplete.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void MarkComplete_OrderDoesNotMatter()
    {
        var progress = new ReplayProgress();

        progress.MarkComplete(ReplayProgress.SourceFamily.Chat);
        progress.MarkComplete(ReplayProgress.SourceFamily.Player);

        progress.ReplayComplete.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void MarkComplete_FiresPropertyChanged()
    {
        var progress = new ReplayProgress();
        var changedProperties = new List<string>();
        ((INotifyPropertyChanged)progress).PropertyChanged += (_, e) =>
            changedProperties.Add(e.PropertyName!);

        progress.MarkComplete(ReplayProgress.SourceFamily.Player);

        changedProperties.Should().Contain(nameof(IReplayProgress.PlayerProgress));
    }

    [Fact]
    public void MarkComplete_CalledTwice_DoesNotThrow()
    {
        var progress = new ReplayProgress();

        progress.MarkComplete(ReplayProgress.SourceFamily.Player);
        var act = () => progress.MarkComplete(ReplayProgress.SourceFamily.Player);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task MarkComplete_ConcurrentCalls_CompletesExactlyOnce()
    {
        var progress = new ReplayProgress();
        var completionCount = 0;
        _ = progress.ReplayComplete.ContinueWith(
            _ => Interlocked.Increment(ref completionCount),
            TaskScheduler.Default);

        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() =>
            {
                if (i % 2 == 0)
                    progress.MarkComplete(ReplayProgress.SourceFamily.Player);
                else
                    progress.MarkComplete(ReplayProgress.SourceFamily.Chat);
            }));

        await Task.WhenAll(tasks);
        await progress.ReplayComplete;

        completionCount.Should().Be(1);
        progress.PlayerProgress.Should().Be(1.0);
        progress.ChatProgress.Should().Be(1.0);
    }
}
