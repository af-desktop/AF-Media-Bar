// 使用 fake backend 验证宿主的串行化、关闭竞态与重启代际；不连接真实 SMTC。
using AFMediaBar.Classes.Services.Media.Smtc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Media;

namespace AFMediaBar.Layout.Tests;

/// <summary>串行 SMTC 宿主的并发边界。</summary>
[TestClass]
public sealed class SmtcSessionHostTests
{
    [TestMethod]
    public async Task BackendCallsAreSerializedOnOneHostThread()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        var first = host.ReadAsync("chrome", SmtcReadScope.Media, default);
        var second = host.ReadAsync("chrome", SmtcReadScope.Media, default);
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, backend.MaximumConcurrentCalls, "宿主必须把 backend 调用串行化。");
        Assert.AreEqual(1, backend.CallingThreads.Count, "所有 backend 调用必须来自同一个宿主线程。");
    }

    [TestMethod]
    public async Task ReadDoesNotPublishState()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        var changes = 0;
        host.StateChanged += _ => Interlocked.Increment(ref changes);

        await host.ReadAsync("chrome", SmtcReadScope.Playback, default);

        Assert.AreEqual(0, changes, "读取是快照构建的一部分，不能反过来触发目录状态事件。");
    }

    [TestMethod]
    public async Task ClosingSessionDuringReadDropsItWithoutThrowing()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        var empty = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += state =>
        {
            if (state.Sessions.Count == 0 && state.Status == SmtcHostStatus.Ready)
                empty.TrySetResult(true);
        };

        backend.BlockReads = true;
        var read = host.ReadAsync("chrome", SmtcReadScope.Media, default);
        await backend.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Sessions.Clear();
        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.SessionsDirty, null));
        backend.ReleaseRead();

        await read;
        await empty.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(host.Find("chrome"));
    }

    [TestMethod]
    public async Task RestartWaitsForInflightReadAndAdvancesGeneration()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        var generation = host.State.Generation;

        backend.BlockReads = true;
        var read = host.ReadAsync("chrome", SmtcReadScope.Media, default);
        await backend.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var restart = host.RestartAsync(default);
        backend.ReleaseRead();

        await read;
        await restart;
        Assert.AreEqual(generation + 1, host.State.Generation);
    }

    [TestMethod]
    public async Task StartFailureLeavesHostFailedAndRestartRecovers()
    {
        var backend = new FakeBackend { StartFailure = new InvalidOperationException("start failed") };
        using var host = new SmtcSessionHost(backend);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.StartAsync(default));
        Assert.AreEqual(SmtcHostStatus.Failed, host.State.Status);

        backend.StartFailure = null;
        await host.RestartAsync(default);
        Assert.AreEqual(SmtcHostStatus.Ready, host.State.Status);
    }

    [TestMethod]
    public async Task DisposeStopsHostAndRejectsLaterReads()
    {
        var backend = new FakeBackend();
        var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        host.Dispose();

        Assert.AreEqual(SmtcHostStatus.Stopped, host.State.Status);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => host.ReadAsync("chrome", SmtcReadScope.Media, default));
    }

    [TestMethod]
    public async Task DisposeCancelsInflightAndQueuedReadsWithoutHanging()
    {
        var backend = new FakeBackend();
        var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        backend.BlockReads = true;
        var inflight = host.ReadAsync("chrome", SmtcReadScope.Media, CancellationToken.None);
        await backend.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = host.ReadAsync("chrome", SmtcReadScope.Media, CancellationToken.None);

        host.Dispose();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => inflight.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(SmtcHostStatus.Stopped, host.State.Status);
    }

    [TestMethod]
    public async Task ReadClosedSessionRemovesItAndReleasesBackend()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        backend.ReturnClosedOnRead = true;

        var read = await host.ReadAsync("chrome", SmtcReadScope.All, default);

        Assert.IsNull(read);
        Assert.IsNull(host.Find("chrome"));
        CollectionAssert.Contains(backend.ReleasedSessions, "chrome");
    }

    [TestMethod]
    public async Task BurstBackendEventsCoalesceIntoSingleStatePublish()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        var published = 0;
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += _ =>
        {
            Interlocked.Increment(ref published);
            seen.TrySetResult(true);
        };

        backend.BlockReads = true;
        var inflight = host.ReadAsync("chrome", SmtcReadScope.Media, default);
        await backend.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.SessionsDirty, null));
        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.FocusedDirty, null));
        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.PlaybackChanged, "chrome"));
        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.MediaChanged, "chrome"));
        backend.ReleaseRead();

        await inflight;
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // 让可能多排队的回调跑完后确认仅发布一次。
        await host.GetOsSessionCountAsync(default);
        Assert.AreEqual(1, Volatile.Read(ref published));
    }

    [TestMethod]
    public async Task StopFailureDoesNotBlockRestartOrDispose()
    {
        var backend = new FakeBackend { StopFailure = new InvalidOperationException("COM disconnected") };
        var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        var generation = host.State.Generation;

        await host.RestartAsync(default);
        Assert.AreEqual(SmtcHostStatus.Ready, host.State.Status);
        Assert.AreEqual(generation + 1, host.State.Generation);

        host.Dispose();
        Assert.AreEqual(SmtcHostStatus.Stopped, host.State.Status);
    }

    [TestMethod]
    public async Task ConcurrentCancellableReadsDoNotHitDisposedTokenRace()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        var tasks = new List<Task<SmtcBackendRead?>>(40);
        var tokens = new List<CancellationTokenSource>(40);
        try
        {
            for (var index = 0; index < 40; index++)
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                tokens.Add(cts);
                tasks.Add(host.ReadAsync("chrome", SmtcReadScope.Media, cts.Token));
            }

            var results = await Task.WhenAll(tasks);
            Assert.IsTrue(results.All(result => result is not null));
        }
        finally
        {
            foreach (var cts in tokens) cts.Dispose();
        }
    }

    [TestMethod]
    public async Task ForceUpdateRefreshesPlaybackOfExistingSessionAndDoesNotFlagClosed()
    {
        var backend = new FakeBackend { IsPlayingOnRead = false };
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        Assert.IsFalse(host.Find("chrome")!.IsPlaying);

        SmtcCatalogState? latest = null;
        host.StateChanged += state => latest = state;
        backend.IsPlayingOnRead = true;
        await host.ForceUpdateAsync(default);

        Assert.IsTrue(host.Find("chrome")!.IsPlaying);
        Assert.IsNotNull(latest);
        Assert.IsFalse(latest.LastChange.HasFlag(SmtcCatalogChangeKind.SessionClosed));
    }

    [TestMethod]
    public async Task StartAsyncIsNoOpWhenAlreadyReady()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);
        var generation = host.State.Generation;
        var revision = host.State.Revision;

        await host.StartAsync(default);

        Assert.AreEqual(generation, host.State.Generation);
        Assert.AreEqual(revision, host.State.Revision);
    }

    [TestMethod]
    public async Task TimelineChangeDoesNotFlagSessionClosedOrMetadata()
    {
        var backend = new FakeBackend();
        using var host = new SmtcSessionHost(backend);
        await host.StartAsync(default);

        var seen = new TaskCompletionSource<SmtcCatalogState>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StateChanged += state => seen.TrySetResult(state);
        backend.Raise(new SmtcBackendEvent(SmtcBackendEventKind.TimelineChanged, "chrome"));

        var state = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(state.LastChange.HasFlag(SmtcCatalogChangeKind.Timeline));
        Assert.IsFalse(state.LastChange.HasFlag(SmtcCatalogChangeKind.SessionClosed));
        Assert.IsFalse(state.LastChange.HasFlag(SmtcCatalogChangeKind.Metadata));
    }

    private sealed class FakeBackend : ISmtcBackend
    {
        private readonly object _sync = new();
        private int _active;
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<SmtcBackendEvent>? Changed;

        public List<SmtcBackendSession> Sessions { get; } = [new("chrome", "chrome")];
        public List<string> ReleasedSessions { get; } = [];
        public bool BlockReads { get; set; }
        public bool ReturnClosedOnRead { get; set; }
        public bool IsPlayingOnRead { get; set; } = true;
        public Exception? StartFailure { get; set; }
        public Exception? StopFailure { get; set; }
        public int MaximumConcurrentCalls { get; private set; }
        public HashSet<int> CallingThreads { get; } = [];
        public Task ReadStarted => _readStarted.Task;

        public Task StartAsync(CancellationToken cancellationToken) =>
            StartFailure is null ? Task.CompletedTask : Task.FromException(StartFailure);

        public Task StopAsync() =>
            StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);

        public Task<IReadOnlyList<SmtcBackendSession>?> GetSessionsAsync(CancellationToken cancellationToken)
        {
            Enter();
            try { return Task.FromResult<IReadOnlyList<SmtcBackendSession>?>(Sessions.ToArray()); }
            finally { Exit(); }
        }

        public async Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BlockReads)
                {
                    _readStarted.TrySetResult();
                    await _readRelease.Task.WaitAsync(cancellationToken);
                    BlockReads = false;
                }

                return new SmtcBackendRead(
                    new SmtcBackendMedia("Title", "Artist", "Album", false),
                    new SmtcBackendPlayback(true, !ReturnClosedOnRead && IsPlayingOnRead, true, true, true, true, false, MediaPlaybackAutoRepeatMode.None, 1, IsClosed: ReturnClosedOnRead),
                    new SmtcBackendTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(100), TimeSpan.Zero, DateTimeOffset.UtcNow),
                    null);
            }
            finally { Exit(); }
        }

        public Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken)
        {
            Enter();
            try { return Task.FromResult(true); }
            finally { Exit(); }
        }

        public Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken) => Task.FromResult(Sessions.Count);

        public Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("chrome");

        public void ReleaseSession(string sessionId) => ReleasedSessions.Add(sessionId);

        public void Raise(SmtcBackendEvent value) => Changed?.Invoke(value);

        public void ReleaseRead() => _readRelease.TrySetResult();

        private void Enter()
        {
            lock (_sync)
            {
                _active++;
                MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, _active);
                CallingThreads.Add(Environment.CurrentManagedThreadId);
            }
        }

        private void Exit()
        {
            lock (_sync) _active--;
        }
    }
}
