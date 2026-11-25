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

    private sealed class FakeBackend : ISmtcBackend
    {
        private readonly object _sync = new();
        private int _active;
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<SmtcBackendEvent>? Changed;

        public List<SmtcBackendSession> Sessions { get; } = [new("chrome", "chrome")];
        public bool BlockReads { get; set; }
        public int MaximumConcurrentCalls { get; private set; }
        public HashSet<int> CallingThreads { get; } = [];
        public Task ReadStarted => _readStarted.Task;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

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
                    new SmtcBackendPlayback(true, true, true, true, true, true, false, MediaPlaybackAutoRepeatMode.None, 1),
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

        public void ReleaseSession(string sessionId) { }

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
