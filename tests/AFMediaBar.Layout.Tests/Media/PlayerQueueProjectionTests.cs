using AFMediaBar.Classes.Models;
using AFMediaBar.ViewModels.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>队列面板只按 Nons 返回的 items 顺序投影，并用播放器索引标记当前项。</summary>
[TestClass]
public sealed class PlayerQueueProjectionTests
{
    [TestMethod]
    public async Task QueueOrderAndCurrentItemComeFromCompanionSnapshot()
    {
        var companion = new FakePlayerCompanion();
        companion.SetCurrent(new PlayerCompanionSnapshot(
            PlayerCompanionConnectionState.Connected,
            new PlayerCompanionCapabilities(true, true, true, false, false, false, false),
            1,
            "1.12.4",
            new PlayerCompanionPlayer(PlayerCompanionPlaybackStatus.Playing, 1, "track-b", false,
                MediaRepeatMode.Off, 0, 0, 0, false),
            [Track("track-a"), Track("track-b"), Track("track-c")],
            [2, 0, 1],
            7,
            PlayerCompanionError.None,
            null,
            false,
            DateTimeOffset.UtcNow));

        using var viewModel = new PlayerQueueViewModel(companion);
        await viewModel.ActivateAsync();

        CollectionAssert.AreEqual(new[] { "track-a", "track-b", "track-c" },
            viewModel.Items.Select(item => item.TrackId).ToArray());
        Assert.AreEqual(1, viewModel.Items.Count(item => item.IsCurrent));
        Assert.AreEqual("track-b", viewModel.CurrentItem!.TrackId);
        Assert.IsTrue(viewModel.Items[1].IsCurrent);
        Assert.AreEqual(string.Empty, viewModel.StatusText);
    }

    [TestMethod]
    public async Task EmptyQueueShowsEmptyStateWithoutInventingCurrentTrack()
    {
        var companion = new FakePlayerCompanion();
        companion.SetCurrent(new PlayerCompanionSnapshot(
            PlayerCompanionConnectionState.Connected,
            new PlayerCompanionCapabilities(true, true, false, false, false, false, false),
            1,
            "1.12.4",
            new PlayerCompanionPlayer(PlayerCompanionPlaybackStatus.Stopped, null, null, false,
                MediaRepeatMode.Off, 0, 0, 0, false),
            [],
            [],
            0,
            PlayerCompanionError.None,
            null,
            false,
            DateTimeOffset.UtcNow));

        using var viewModel = new PlayerQueueViewModel(companion);
        await viewModel.ActivateAsync();

        Assert.AreEqual(0, viewModel.Items.Count);
        Assert.IsNull(viewModel.CurrentItem);
        Assert.IsTrue(viewModel.IsEmpty);
    }

    [TestMethod]
    public async Task StateUpdateWithSameQueueRevisionDoesNotRebuildItemsOrNotifyItemsChanged()
    {
        var companion = new FakePlayerCompanion();
        var initial = new PlayerCompanionSnapshot(
            PlayerCompanionConnectionState.Connected,
            new PlayerCompanionCapabilities(true, true, true, false, false, false, false),
            1,
            "1.12.4",
            new PlayerCompanionPlayer(PlayerCompanionPlaybackStatus.Playing, 0, "track-a", false,
                MediaRepeatMode.Off, 0, 0, 0, false),
            [Track("track-a"), Track("track-b")],
            [0, 1],
            7,
            PlayerCompanionError.None,
            null,
            false,
            DateTimeOffset.UtcNow);
        companion.SetCurrent(initial);

        using var viewModel = new PlayerQueueViewModel(companion);
        await viewModel.ActivateAsync();
        var initialItems = viewModel.Items;
        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        companion.SetCurrent(initial with
        {
            Player = initial.Player! with { Index = 1, CurrentTrackId = "track-b" }
        });

        Assert.AreSame(initialItems, viewModel.Items, "队列版本号未变时不得重建列表项集合。");
        Assert.IsFalse(notified.Contains(nameof(PlayerQueueViewModel.Items)));
        Assert.IsFalse(notified.Contains(string.Empty));
        Assert.AreEqual("track-b", viewModel.CurrentItem!.TrackId);
    }

    private static PlayerCompanionTrack Track(string id) => new(
        id,
        id,
        ["Artist"],
        "Album",
        180000,
        "netease",
        new PlayerCompanionReference("netease", "track", id),
        null);
}
