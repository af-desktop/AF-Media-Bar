// SMTC 后端与宿主的普通数据契约；WinRT 对象只存在于 WindowsSmtcBackend 内部。
using Windows.Media;

namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>一次读取需要覆盖的数据范围。/ The data scopes covered by one read.</summary>
[Flags]
internal enum SmtcReadScope
{
    None = 0,
    Playback = 1,
    Media = 2,
    Timeline = 4,
    Artwork = 8,
    All = Playback | Media | Timeline | Artwork
}

/// <summary>可以转发给当前会话的媒体命令。/ Media commands that can be sent to the active session.</summary>
internal enum SmtcCommand
{
    TogglePlayPause,
    SkipPrevious,
    SkipNext,
    Seek,
    CycleRepeat
}

/// <summary>后端发现的会话身份，不包含 WinRT 对象。/ Session identity discovered by the backend, without WinRT objects.</summary>
internal sealed record SmtcBackendSession(string Id, string SourceAppId);

/// <summary>后端读取的播放状态。/ Playback state read by the backend.</summary>
internal sealed record SmtcBackendPlayback(
    bool IsKnown,
    bool IsPlaying,
    bool CanTogglePlayPause,
    bool CanSkipPrevious,
    bool CanSkipNext,
    bool CanSeek,
    bool CanRepeat,
    MediaPlaybackAutoRepeatMode RepeatMode,
    double PlaybackRate);

/// <summary>后端读取的媒体元数据。/ Media metadata read by the backend.</summary>
internal sealed record SmtcBackendMedia(string Title, string Artist, string Album, bool IsVideo);

/// <summary>后端读取的时间轴。/ Timeline read by the backend.</summary>
internal sealed record SmtcBackendTimeline(TimeSpan Start, TimeSpan End, TimeSpan Position, DateTimeOffset LastUpdated);

/// <summary>一次后端读取的普通值结果；封面只传字节，不传 WinRT 流。</summary>
internal sealed record SmtcBackendRead(
    SmtcBackendMedia Media,
    SmtcBackendPlayback Playback,
    SmtcBackendTimeline Timeline,
    byte[]? Artwork);

/// <summary>后端事件类型；事件只带 ID，不携带 WinRT 参数。</summary>
internal enum SmtcBackendEventKind
{
    SessionsDirty,
    FocusedDirty,
    PlaybackChanged,
    MediaChanged,
    TimelineChanged,
    Closed
}

/// <summary>后端事件；可从任意线程触发，宿主只投递，不在这里执行 COM。</summary>
internal sealed record SmtcBackendEvent(SmtcBackendEventKind Kind, string? SessionId);

/// <summary>
/// SMTC 原生操作的抽象。所有成员只允许宿主线程调用；事件可以从任意线程触发，
/// 回调只负责通知，宿主再回到自己的线程读取 COM。
/// </summary>
internal interface ISmtcBackend
{
    event Action<SmtcBackendEvent>? Changed;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task<IReadOnlyList<SmtcBackendSession>?> GetSessionsAsync(CancellationToken cancellationToken);
    Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken);
    Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken);
    Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken);
    Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken);
}

