// 直接封装 Windows.Media.Control；所有成员只由 SmtcSessionHost 的宿主线程调用。
using System.IO;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>
/// 生产 SMTC 后端：持有 manager 与原始会话，只在宿主线程读取属性、发送命令；
/// WinRT 事件回调只上报会话 ID，不在这里读取 COM。
/// </summary>
internal sealed class WindowsSmtcBackend : ISmtcBackend
{
    private const int MaximumArtworkBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, SessionRegistration> _sessions = new(StringComparer.Ordinal);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _started;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _currentSessionChanged;

    public event Action<SmtcBackendEvent>? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return;
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        _manager = manager;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessionsChanged = (_, _) => Raise(new SmtcBackendEvent(SmtcBackendEventKind.SessionsDirty, null));
            _currentSessionChanged = (_, _) => Raise(new SmtcBackendEvent(SmtcBackendEventKind.FocusedDirty, null));
            manager.SessionsChanged += _sessionsChanged;
            manager.CurrentSessionChanged += _currentSessionChanged;
            _started = true;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public Task StopAsync()
    {
        _started = false;
        var manager = _manager;
        var sessionsChanged = _sessionsChanged;
        var currentSessionChanged = _currentSessionChanged;
        _manager = null;
        _sessionsChanged = null;
        _currentSessionChanged = null;

        if (manager is not null)
        {
            // COM 已断开时退订可能抛异常；必须隔离，否则 RestartAsync/Dispose 会永远卡在失效 manager 上。
            if (sessionsChanged is not null)
            {
                try { manager.SessionsChanged -= sessionsChanged; }
                catch (Exception) { }
            }
            if (currentSessionChanged is not null)
            {
                try { manager.CurrentSessionChanged -= currentSessionChanged; }
                catch (Exception) { }
            }
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            try { session.Detach(); }
            catch (Exception) { }
        }
        _sessions.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SmtcBackendSession>?> GetSessionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started || _manager is null)
            return Task.FromResult<IReadOnlyList<SmtcBackendSession>?>(null);

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> rawSessions;
        try
        {
            rawSessions = _manager.GetSessions();
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<SmtcBackendSession>?>(null);
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SmtcBackendSession>(rawSessions.Count);
        foreach (var raw in rawSessions)
        {
            string id;
            try
            {
                id = raw.SourceAppUserModelId;
            }
            catch (Exception)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(id) || present.Contains(id))
                continue;

            if (_sessions.TryGetValue(id, out var existing))
            {
                if (!ReferenceEquals(existing.Session, raw))
                {
                    existing.Detach();
                    existing.Session = raw;
                    if (!existing.TryAttach())
                    {
                        _sessions.Remove(id);
                        continue;
                    }
                }
            }
            else
            {
                var registration = new SessionRegistration(id, raw, Raise);
                if (!registration.TryAttach())
                    continue;
                _sessions[id] = registration;
            }

            present.Add(id);
            result.Add(new SmtcBackendSession(id, id));
        }

        foreach (var id in _sessions.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _sessions[id].Detach();
            _sessions.Remove(id);
        }

        return Task.FromResult<IReadOnlyList<SmtcBackendSession>?>(result);
    }

    public async Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken)
    {
        if (!_started || !_sessions.TryGetValue(sessionId, out var registration))
            return null;
        var session = registration.Session;
        try
        {
            // 先读播放状态：会话关闭时直接返回 Closed，避免媒体属性读取先失败而被误判为暂时不可用。
            var playback = scope.HasFlag(SmtcReadScope.Playback)
                ? ReadPlayback(session)
                : new SmtcBackendPlayback(false, false, false, false, false, false, false, MediaPlaybackAutoRepeatMode.None, 1);

            if (playback.IsClosed)
            {
                return new SmtcBackendRead(
                    new SmtcBackendMedia(string.Empty, string.Empty, string.Empty, false),
                    playback,
                    new SmtcBackendTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue),
                    null);
            }

            SmtcBackendMedia media = new(string.Empty, string.Empty, string.Empty, false);
            GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
            if (scope.HasFlag(SmtcReadScope.Media) || scope.HasFlag(SmtcReadScope.Artwork))
            {
                properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (properties is null)
                    return null;
                media = new SmtcBackendMedia(
                    properties.Title ?? string.Empty,
                    properties.Artist ?? string.Empty,
                    properties.AlbumTitle ?? string.Empty,
                    properties.PlaybackType == MediaPlaybackType.Video);
            }

            var timeline = scope.HasFlag(SmtcReadScope.Timeline)
                ? ReadTimeline(session)
                : new SmtcBackendTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);

            byte[]? artwork = null;
            if (scope.HasFlag(SmtcReadScope.Artwork) && properties?.Thumbnail is { } thumbnail)
                artwork = await ReadArtworkAsync(thumbnail, cancellationToken);

            return new SmtcBackendRead(media, playback, timeline, artwork);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 会话在读取过程中关闭或 COM 失效：本轮按未命中处理，由宿主移除或等待下一次事件。
            return null;
        }
    }

    public async Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken)
    {
        if (!_started || !_sessions.TryGetValue(sessionId, out var registration))
            return false;
        var session = registration.Session;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (command)
            {
                case SmtcCommand.TogglePlayPause:
                    var playback = session.GetPlaybackInfo();
                    return playback.Controls?.IsPlayPauseToggleEnabled == true && await session.TryTogglePlayPauseAsync().AsTask(cancellationToken);
                case SmtcCommand.SkipPrevious:
                    return await session.TrySkipPreviousAsync().AsTask(cancellationToken);
                case SmtcCommand.SkipNext:
                    return await session.TrySkipNextAsync().AsTask(cancellationToken);
                case SmtcCommand.Seek when positionSeconds is { } seconds:
                    var seekPlayback = session.GetPlaybackInfo();
                    if (seekPlayback.Controls?.IsPlaybackPositionEnabled != true)
                        return false;
                    var timeline = session.GetTimelineProperties();
                    var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                    if (duration <= 0)
                        return false;
                    var target = timeline.StartTime + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration));
                    return await session.TryChangePlaybackPositionAsync(target.Ticks).AsTask(cancellationToken);
                case SmtcCommand.CycleRepeat:
                    var current = session.GetPlaybackInfo();
                    if (current.Controls?.IsRepeatEnabled != true)
                        return false;
                    var next = current.AutoRepeatMode switch
                    {
                        MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                        MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
                        _ => MediaPlaybackAutoRepeatMode.List
                    };
                    return await session.TryChangeAutoRepeatModeAsync(next).AsTask(cancellationToken);
                default:
                    return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_started && _manager is not null ? _manager.GetSessions().Count : -1);
        }
        catch (Exception)
        {
            return Task.FromResult(-1);
        }
    }

    public Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_started ? _manager?.GetCurrentSession()?.SourceAppUserModelId : null);
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public void ReleaseSession(string sessionId)
    {
        if (!_sessions.Remove(sessionId, out var registration))
            return;
        try { registration.Detach(); }
        catch (Exception) { }
    }

    private static SmtcBackendPlayback ReadPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        var controls = playback.Controls;
        return new SmtcBackendPlayback(
            true,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            controls?.IsPlayPauseToggleEnabled ?? false,
            controls?.IsPreviousEnabled ?? false,
            controls?.IsNextEnabled ?? false,
            controls?.IsPlaybackPositionEnabled ?? false,
            controls?.IsRepeatEnabled ?? false,
            playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None,
            playback.PlaybackRate is > 0 ? playback.PlaybackRate.Value : 1,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
            playback.PlaybackType == MediaPlaybackType.Video,
            playback.AutoRepeatMode is not null);
    }

    private static SmtcBackendTimeline ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        var timeline = session.GetTimelineProperties();
        return new SmtcBackendTimeline(
            timeline.StartTime,
            timeline.EndTime,
            timeline.Position,
            timeline.LastUpdatedTime);
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference thumbnail, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
            using var input = stream.AsStreamForRead();
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (output.Length < MaximumArtworkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumArtworkBytes - output.Length)), cancellationToken);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
            }

            return output.Length == 0 ? null : output.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Raise(SmtcBackendEvent value)
    {
        if (!_started) return;
        try { Changed?.Invoke(value); }
        catch (Exception) { }
    }

    private sealed class SessionRegistration
    {
        private readonly Action<SmtcBackendEvent> _raise;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playback;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _media;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timeline;

        internal SessionRegistration(string id, GlobalSystemMediaTransportControlsSession session, Action<SmtcBackendEvent> raise)
        {
            Id = id;
            Session = session;
            _raise = raise;
        }

        internal string Id { get; }
        internal GlobalSystemMediaTransportControlsSession Session { get; set; }

        internal bool TryAttach()
        {
            _playback = (_, _) => _raise(new SmtcBackendEvent(SmtcBackendEventKind.PlaybackChanged, Id));
            _media = (_, _) => _raise(new SmtcBackendEvent(SmtcBackendEventKind.MediaChanged, Id));
            _timeline = (_, _) => _raise(new SmtcBackendEvent(SmtcBackendEventKind.TimelineChanged, Id));
            try
            {
                Session.PlaybackInfoChanged += _playback;
                Session.MediaPropertiesChanged += _media;
                Session.TimelinePropertiesChanged += _timeline;
                return true;
            }
            catch (Exception)
            {
                Detach();
                return false;
            }
        }

        internal void Detach()
        {
            // 已关闭会话的 WinRT COM 对象可能已断开；逐个退订并隔离异常，避免中断整批枚举。
            if (_playback is { } playback)
            {
                _playback = null;
                try { Session.PlaybackInfoChanged -= playback; }
                catch (Exception) { }
            }
            if (_media is { } media)
            {
                _media = null;
                try { Session.MediaPropertiesChanged -= media; }
                catch (Exception) { }
            }
            if (_timeline is { } timeline)
            {
                _timeline = null;
                try { Session.TimelinePropertiesChanged -= timeline; }
                catch (Exception) { }
            }
        }
    }
}
