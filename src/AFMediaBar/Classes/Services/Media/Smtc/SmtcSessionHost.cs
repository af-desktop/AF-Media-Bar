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
    private readonly ConcurrentQueue<SmtcBackendEvent> _events = new();
    private readonly Dictionary<string, SmtcSession> _sessions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SmtcCatalogState _state = SmtcCatalogState.Initial;
    private string? _focusedSessionId;
    private int _generation;
    private int _revision;
    private int _pumpScheduled;
    private int _isStopped;

    internal SmtcSessionHost(ISmtcBackend backend)
    {
        _backend = backend;
        _backend.Changed += OnBackendChanged;

        Dispatcher? dispatcher = null;
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ready.TrySetResult();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AF SMTC"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
        _dispatcher = dispatcher ?? throw new InvalidOperationException("SMTC dispatcher was not created.");
    }

    internal event Action<SmtcCatalogState>? StateChanged;

    internal SmtcCatalogState State => Volatile.Read(ref _state);

    internal SmtcSession? Find(string sessionId) =>
        State.Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isStopped) != 0) return;
        await RunAsync(async () =>
        {
            SetStatus(SmtcHostStatus.Starting);
            try
            {
                await _backend.StartAsync(cancellationToken);
                _generation++;
                await RefreshSessionsCoreAsync(cancellationToken);
                SetStatus(SmtcHostStatus.Ready);
            }
            catch
            {
                SetStatus(SmtcHostStatus.Failed);
                throw;
            }
        }, cancellationToken);
    }

    internal Task RestartAsync(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        SetStatus(SmtcHostStatus.Restarting);
        try
        {
            await _backend.StopAsync();
            CloseAllSessions();
            _generation++;
            await _backend.StartAsync(cancellationToken);
            await RefreshSessionsCoreAsync(cancellationToken);
            SetStatus(SmtcHostStatus.Ready);
        }
        catch
        {
            SetStatus(SmtcHostStatus.Failed);
            throw;
        }
    }, cancellationToken);

    internal Task<IReadOnlyList<SmtcBackendSession>> RefreshAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await RefreshSessionsCoreAsync(cancellationToken);
            return (IReadOnlyList<SmtcBackendSession>)State.Sessions
                .Select(session => new SmtcBackendSession(session.Id, session.SourceAppId))
                .ToArray();
        }, cancellationToken);

    internal Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _backend.GetOsSessionCountAsync(cancellationToken), cancellationToken);

    internal Task ForceUpdateAsync(CancellationToken cancellationToken) =>
        RunAsync(() => RefreshSessionsCoreAsync(cancellationToken), cancellationToken);

    internal Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || !session.IsOpen)
                return null;
            var read = await _backend.ReadAsync(sessionId, scope, cancellationToken);
            if (read is null || read.Playback.IsClosed)
            {
                RemoveSession(sessionId);
                return null;
            }

            if (scope.HasFlag(SmtcReadScope.Playback))
            {
                session.ApplyPlayback(read.Playback);
                Publish();
            }

            return read;
        }, cancellationToken);

    internal Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || !session.IsOpen)
                return false;
            return await _backend.ExecuteAsync(sessionId, command, positionSeconds, cancellationToken);
        }, cancellationToken);

    internal Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _backend.GetFocusedSessionIdAsync(cancellationToken), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isStopped, 1) != 0)
            return;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _gate.WaitAsync();
                    try { await _backend.StopAsync(); }
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

        try { done.Task.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        CloseAllSessions();
        SetStatus(SmtcHostStatus.Stopped);
        _backend.Changed -= OnBackendChanged;
        try { _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); } catch (Exception) { }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _stopped.TrySetResult();
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isStopped) != 0)
            return await Task.FromCanceled<T>(new CancellationToken(canceled: true));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _gate.WaitAsync(cancellationToken);
                    try { completion.TrySetResult(await action()); }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return await completion.Task;
    }

    private async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            await action();
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
        if (Interlocked.Exchange(ref _pumpScheduled, 1) != 0) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _gate.WaitAsync();
                    try { await PumpAsync(); }
                    finally { _gate.Release(); }
                }
                catch (Exception) { }
                finally
                {
                    Interlocked.Exchange(ref _pumpScheduled, 0);
                    if (!_events.IsEmpty) SchedulePump();
                }
            }));
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _pumpScheduled, 0);
        }
    }

    private async Task PumpAsync()
    {
        var sessionsDirty = false;
        var focusedDirty = false;
        var playback = new HashSet<string>(StringComparer.Ordinal);
        var metadataDirty = false;
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
                case SmtcBackendEventKind.TimelineChanged:
                case SmtcBackendEventKind.Closed:
                    metadataDirty = true;
                    break;
            }
        }

        if (sessionsDirty)
            await RefreshSessionsCoreAsync(CancellationToken.None);
        if (focusedDirty)
        {
            _focusedSessionId = await _backend.GetFocusedSessionIdAsync(CancellationToken.None);
            Publish();
        }
        foreach (var id in playback)
        {
            if (!_sessions.TryGetValue(id, out var session)) continue;
            var read = await _backend.ReadAsync(id, SmtcReadScope.Playback, CancellationToken.None);
            if (read is null || read.Playback.IsClosed)
            {
                RemoveSession(id);
                continue;
            }
            session.ApplyPlayback(read.Playback);
            Publish();
        }
        if (metadataDirty) Publish();
    }

    private async Task RefreshSessionsCoreAsync(CancellationToken cancellationToken)
    {
        var discovered = await _backend.GetSessionsAsync(cancellationToken);
        if (discovered is null) return;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in discovered)
        {
            if (!present.Add(item.Id)) continue;
            if (!_sessions.TryGetValue(item.Id, out var session))
            {
                session = new SmtcSession(item.Id, item.SourceAppId);
                _sessions[item.Id] = session;
                var initial = await _backend.ReadAsync(item.Id, SmtcReadScope.Playback, cancellationToken);
                if (initial is not null) session.ApplyPlayback(initial.Playback);
            }
        }

        foreach (var id in _sessions.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _sessions[id].MarkClosed();
            _sessions.Remove(id);
        }

        _focusedSessionId = await _backend.GetFocusedSessionIdAsync(cancellationToken);
        Publish();
    }

    private void CloseAllSessions()
    {
        foreach (var session in _sessions.Values)
            session.MarkClosed();
        _sessions.Clear();
        _focusedSessionId = null;
        Publish();
    }

    private void RemoveSession(string sessionId)
    {
        if (_sessions.Remove(sessionId, out var session))
            session.MarkClosed();
        Publish();
    }

    private void SetStatus(SmtcHostStatus status)
    {
        _state = _state with { Status = status, Revision = ++_revision, FocusedSessionId = _focusedSessionId };
        RaiseState();
    }

    private void Publish()
    {
        _state = new SmtcCatalogState(
            _generation,
            ++_revision,
            _state.Status,
            _focusedSessionId,
            _sessions.Values.Where(session => session.IsOpen).ToArray());
        RaiseState();
    }

    private void RaiseState()
    {
        try { StateChanged?.Invoke(_state); }
        catch (Exception) { }
    }
}
