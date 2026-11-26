// 串行 SMTC 宿主：专用 STA 线程、操作闸门与脏标记泵；所有 COM 只在这里执行。
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>
/// 拥有 SMTC backend 和全部 COM 调用的单所有者。公开操作只投递到宿主线程；
/// WinRT 事件只进入脏标记队列，由宿主合并后读取，避免 UI/事件线程并发碰同一会话。
/// </summary>
internal sealed class SmtcSessionHost : IDisposable
{
    private readonly ISmtcBackend _backend;
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly ConcurrentQueue<SmtcBackendEvent> _events = new();
    private readonly Dictionary<string, SmtcSession> _sessions = new(StringComparer.Ordinal);
    private SmtcCatalogState _state = SmtcCatalogState.Initial;
    private string? _focusedSessionId;
    private int _generation;
    private int _revision;
    private int _pumpScheduled;
    private int _isStopped;

    internal SmtcSessionHost(ISmtcBackend backend)
    {
        _backend = backend;

        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AF SMTC"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher ?? throw new InvalidOperationException("SMTC dispatcher was not created.");
        // 确保 _dispatcher 就绪后再接管后端事件，避免极端早到事件撞上空 dispatcher。
        _backend.Changed += OnBackendChanged;
    }

    internal event Action<SmtcCatalogState>? StateChanged;

    internal SmtcCatalogState State => Volatile.Read(ref _state);

    internal SmtcSession? Find(string sessionId) =>
        State.Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isStopped) != 0) return;
        await RunAsync(async token =>
        {
            // 看门狗可能已在重试间隔内通过 RestartAsync 恢复宿主；就绪后重复启动直接返回。
            if (_state.Status == SmtcHostStatus.Ready) return;
            SetStatus(SmtcHostStatus.Starting);
            try
            {
                await _backend.StartAsync(token);
                _generation++;
                var change = await RefreshSessionsCoreAsync(token, publish: false);
                SetStatus(SmtcHostStatus.Ready, change);
            }
            catch
            {
                if (Volatile.Read(ref _isStopped) == 0)
                    SetStatus(SmtcHostStatus.Failed);
                throw;
            }
        }, cancellationToken);
    }

    internal Task RestartAsync(CancellationToken cancellationToken) => RunAsync(async token =>
    {
        SetStatus(SmtcHostStatus.Restarting);
        try
        {
            try { await _backend.StopAsync(); }
            catch (Exception) { }
            var closed = CloseAllSessions(publish: false);
            _generation++;
            await _backend.StartAsync(token);
            var refreshed = await RefreshSessionsCoreAsync(token, publish: false);
            SetStatus(SmtcHostStatus.Ready, closed | refreshed);
        }
        catch
        {
            if (Volatile.Read(ref _isStopped) == 0)
                SetStatus(SmtcHostStatus.Failed);
            throw;
        }
    }, cancellationToken);

    internal Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken) =>
        RunAsync(token => _backend.GetOsSessionCountAsync(token), cancellationToken);

    internal Task ForceUpdateAsync(CancellationToken cancellationToken) =>
        RunAsync(async token => { await RefreshSessionsCoreAsync(token, publish: true); }, cancellationToken);

    internal Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || !session.IsOpen)
                return null;
            var read = await _backend.ReadAsync(sessionId, scope, token);
            if (read is null)
            {
                session.MarkPlaybackUnknown();
                return null;
            }
            if (read.Playback.IsClosed)
            {
                RemoveSession(sessionId);
                return null;
            }

            if (scope.HasFlag(SmtcReadScope.Playback))
            {
                session.ApplyPlayback(read.Playback);
                // 读取由快照构建/看门狗发起；不能在这里发布状态，否则 Service 的 revision 会
                // 把本次构建判成过期，并在每次构建后再次排队，形成自反馈。
            }

            return read;
        }, cancellationToken);

    internal Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken) =>
        RunAsync(async token =>
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || !session.IsOpen)
                return false;
            return await _backend.ExecuteAsync(sessionId, command, positionSeconds, token);
        }, cancellationToken);

    internal Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken) =>
        RunAsync(token => _backend.GetFocusedSessionIdAsync(token), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isStopped, 1) != 0)
            return;

        // 先取消所有在途和排队中的宿主操作，避免卡住的 WinRT 调用拖死退出闸门。
        _stopCancellation.Cancel();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _gate.WaitAsync();
                    try
                    {
                        try { await _backend.StopAsync(); }
                        catch (Exception) { }
                        _backend.Changed -= OnBackendChanged;
                        while (_events.TryDequeue(out _)) { }
                        CloseAllSessions(publish: false);
                        SetStatus(SmtcHostStatus.Stopped);
                    }
                    finally { _gate.Release(); }
                }
                catch (Exception) { }
                finally { done.TrySetResult(); }
            }));
        }
        catch (Exception)
        {
            done.TrySetResult();
        }

        try
        {
            // 收尾必须留在宿主线程；超时后交给后台续体补做关停，调用线程不再碰 _sessions。
            if (!done.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                _ = done.Task.ContinueWith(_ =>
                {
                    try { _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch (Exception) { }
                    try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
        }
        catch (Exception)
        {
            return;
        }

        try { _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch (Exception) { }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isStopped) != 0)
            return await Task.FromCanceled<T>(new CancellationToken(canceled: true));

        // 由调用方和宿主侧双引用计数释放，避免快速完成时宿主先 Dispose 导致 WaitAsync 抛 ObjectDisposedException。
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCancellation.Token);
        var token = linked.Token;
        var linkedOwners = 2;
        var hostReleased = 0;
        void ReleaseLinked()
        {
            if (Interlocked.Decrement(ref linkedOwners) == 0)
                linked.Dispose();
        }
        void ReleaseHostLinked()
        {
            if (Interlocked.Exchange(ref hostReleased, 1) == 0)
                ReleaseLinked();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var operation = _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (Volatile.Read(ref _isStopped) != 0 || token.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(token);
                        return;
                    }

                    await _gate.WaitAsync(token);
                    try
                    {
                        if (Volatile.Read(ref _isStopped) != 0 || token.IsCancellationRequested)
                        {
                            completion.TrySetCanceled(token);
                            return;
                        }

                        completion.TrySetResult(await action(token));
                    }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { ReleaseHostLinked(); }
            }));
            operation.Aborted += (_, _) =>
            {
                completion.TrySetCanceled(token);
                ReleaseHostLinked();
            };
            if (operation.Status == DispatcherOperationStatus.Aborted)
            {
                completion.TrySetCanceled(token);
                ReleaseHostLinked();
            }
        }
        catch (Exception exception)
        {
            ReleaseHostLinked();
            completion.TrySetException(exception);
        }

        _ = completion.Task.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return await completion.Task.WaitAsync(token);
        }
        finally
        {
            ReleaseLinked();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await RunAsync(async token =>
        {
            await action(token);
            return true;
        }, cancellationToken);
    }

    private void OnBackendChanged(SmtcBackendEvent value)
    {
        if (Volatile.Read(ref _isStopped) != 0) return;
        _events.Enqueue(value);
        SchedulePump();
    }

    private void SchedulePump()
    {
        if (Volatile.Read(ref _isStopped) != 0 || Interlocked.Exchange(ref _pumpScheduled, 1) != 0) return;
        try
        {
            _ = _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (Volatile.Read(ref _isStopped) != 0) return;
                    await _gate.WaitAsync(_stopCancellation.Token);
                    try
                    {
                        if (Volatile.Read(ref _isStopped) == 0)
                            await PumpAsync(_stopCancellation.Token);
                    }
                    finally { _gate.Release(); }
                }
                catch (Exception) { }
                finally
                {
                    Interlocked.Exchange(ref _pumpScheduled, 0);
                    if (Volatile.Read(ref _isStopped) == 0 && !_events.IsEmpty) SchedulePump();
                }
            }));
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _pumpScheduled, 0);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var sessionsDirty = false;
        var focusedDirty = false;
        var playback = new HashSet<string>(StringComparer.Ordinal);
        var metadataDirty = false;
        var timelineDirty = false;
        while (_events.TryDequeue(out var value))
        {
            switch (value.Kind)
            {
                case SmtcBackendEventKind.SessionsDirty:
                    sessionsDirty = true;
                    break;
                case SmtcBackendEventKind.FocusedDirty:
                    focusedDirty = true;
                    break;
                case SmtcBackendEventKind.PlaybackChanged when value.SessionId is { } id:
                    playback.Add(id);
                    break;
                case SmtcBackendEventKind.MediaChanged:
                    metadataDirty = true;
                    break;
                case SmtcBackendEventKind.TimelineChanged:
                    timelineDirty = true;
                    break;
            }
        }

        // 同批事件合并为末尾一次 Publish，并保留准确的变化掩码，避免单次突发连续推高 revision 或误武装看门狗。
        var change = SmtcCatalogChangeKind.None;
        if (sessionsDirty)
        {
            change |= await RefreshSessionsCoreAsync(cancellationToken, publish: false);
            if (change == SmtcCatalogChangeKind.None)
                change |= SmtcCatalogChangeKind.Sessions;
        }
        else
        {
            if (focusedDirty)
            {
                _focusedSessionId = await _backend.GetFocusedSessionIdAsync(cancellationToken);
                change |= SmtcCatalogChangeKind.Focused;
            }

            foreach (var id in playback)
            {
                if (!_sessions.TryGetValue(id, out var session)) continue;
                var read = await _backend.ReadAsync(id, SmtcReadScope.Playback, cancellationToken);
                if (read is null)
                {
                    session.MarkPlaybackUnknown();
                    continue;
                }
                if (read.Playback.IsClosed)
                {
                    RemoveSession(id, publish: false);
                    change |= SmtcCatalogChangeKind.Sessions | SmtcCatalogChangeKind.SessionClosed;
                    continue;
                }
                session.ApplyPlayback(read.Playback);
                change |= SmtcCatalogChangeKind.Playback;
            }
        }

        if (metadataDirty) change |= SmtcCatalogChangeKind.Metadata;
        if (timelineDirty) change |= SmtcCatalogChangeKind.Timeline;
        if (change != SmtcCatalogChangeKind.None && Volatile.Read(ref _isStopped) == 0)
            Publish(change);
    }

    private async Task<SmtcCatalogChangeKind> RefreshSessionsCoreAsync(CancellationToken cancellationToken, bool publish = true)
    {
        var discovered = await _backend.GetSessionsAsync(cancellationToken);
        if (discovered is null) return SmtcCatalogChangeKind.None;

        var change = SmtcCatalogChangeKind.None;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in discovered)
        {
            if (!present.Add(item.Id)) continue;
            var isNew = !_sessions.TryGetValue(item.Id, out var session);
            if (isNew)
            {
                session = new SmtcSession(item.Id, item.SourceAppId);
                _sessions[item.Id] = session;
                change |= SmtcCatalogChangeKind.Sessions;
            }

            // 同步刷新所有在场会话的播放状态：覆盖同 AUMID 会话替换、事件丢失与 MarkPlaybackUnknown 的自愈。
            var wasPlaying = session!.IsPlaying;
            var wasKnown = session.IsPlaybackKnown;
            var playbackRead = await _backend.ReadAsync(item.Id, SmtcReadScope.Playback, cancellationToken);
            if (playbackRead is not null)
            {
                if (playbackRead.Playback.IsClosed)
                {
                    RemoveSession(item.Id, publish: false);
                    present.Remove(item.Id);
                    if (!isNew) change |= SmtcCatalogChangeKind.Sessions | SmtcCatalogChangeKind.SessionClosed;
                    continue;
                }

                session.ApplyPlayback(playbackRead.Playback);
                if (wasPlaying != session.IsPlaying || wasKnown != session.IsPlaybackKnown)
                    change |= SmtcCatalogChangeKind.Playback;
            }
        }

        foreach (var id in _sessions.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _sessions[id].MarkClosed();
            _sessions.Remove(id);
            change |= SmtcCatalogChangeKind.Sessions | SmtcCatalogChangeKind.SessionClosed;
        }

        var focused = await _backend.GetFocusedSessionIdAsync(cancellationToken);
        if (!string.Equals(_focusedSessionId, focused, StringComparison.Ordinal))
        {
            _focusedSessionId = focused;
            change |= SmtcCatalogChangeKind.Focused;
        }

        if (publish) Publish(change == SmtcCatalogChangeKind.None ? SmtcCatalogChangeKind.Playback : change);
        return change;
    }

    private SmtcCatalogChangeKind CloseAllSessions(bool publish = true)
    {
        var hadSessions = _sessions.Count > 0;
        foreach (var session in _sessions.Values)
            session.MarkClosed();
        _sessions.Clear();
        _focusedSessionId = null;
        var change = hadSessions
            ? SmtcCatalogChangeKind.Sessions | SmtcCatalogChangeKind.SessionClosed
            : SmtcCatalogChangeKind.None;
        if (publish) Publish(change);
        return change;
    }

    private void RemoveSession(string sessionId, bool publish = true)
    {
        try { _backend.ReleaseSession(sessionId); }
        catch (Exception) { }
        var removed = _sessions.Remove(sessionId, out var session);
        if (removed)
            session!.MarkClosed();
        if (publish)
            Publish(removed ? SmtcCatalogChangeKind.Sessions | SmtcCatalogChangeKind.SessionClosed : SmtcCatalogChangeKind.None);
    }

    private void SetStatus(SmtcHostStatus status, SmtcCatalogChangeKind extraChange = SmtcCatalogChangeKind.None)
    {
        _state = new SmtcCatalogState(
            _generation,
            ++_revision,
            status,
            _focusedSessionId,
            _sessions.Values.Where(session => session.IsOpen).ToArray(),
            SmtcCatalogChangeKind.Status | extraChange);
        RaiseState();
    }

    private void Publish(SmtcCatalogChangeKind change)
    {
        _state = new SmtcCatalogState(
            _generation,
            ++_revision,
            _state.Status,
            _focusedSessionId,
            _sessions.Values.Where(session => session.IsOpen).ToArray(),
            change);
        RaiseState();
    }

    private void RaiseState()
    {
        try { StateChanged?.Invoke(_state); }
        catch (Exception) { }
    }
}
