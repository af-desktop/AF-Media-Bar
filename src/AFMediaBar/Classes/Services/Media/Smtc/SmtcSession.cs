// 应用侧 SMTC 会话句柄与不可变目录状态；不包含 WinRT 对象。
namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>宿主目录状态；UI 只读取这个不可变快照。</summary>
internal enum SmtcHostStatus
{
    Starting,
    Ready,
    Restarting,
    Failed,
    Stopped
}

/// <summary>应用侧会话句柄；播放状态由宿主更新，UI 只读缓存。</summary>
internal sealed class SmtcSession
{
    private int _isOpen = 1;
    private int _isPlaying;
    private int _isPlaybackKnown;

    internal SmtcSession(string id, string sourceAppId)
    {
        Id = id;
        SourceAppId = sourceAppId;
    }

    internal string Id { get; }
    internal string SourceAppId { get; }

    internal bool IsOpen => Volatile.Read(ref _isOpen) != 0;
    internal bool IsPlaying => Volatile.Read(ref _isPlaying) != 0;
    internal bool IsPlaybackKnown => Volatile.Read(ref _isPlaybackKnown) != 0;

    internal void ApplyPlayback(SmtcBackendPlayback playback)
    {
        Volatile.Write(ref _isPlaying, playback.IsPlaying ? 1 : 0);
        Volatile.Write(ref _isPlaybackKnown, playback.IsKnown ? 1 : 0);
    }

    internal void MarkPlaybackUnknown() => Volatile.Write(ref _isPlaybackKnown, 0);

    internal void MarkClosed()
    {
        Volatile.Write(ref _isOpen, 0);
        Volatile.Write(ref _isPlaying, 0);
        Volatile.Write(ref _isPlaybackKnown, 0);
    }
}

/// <summary>最近一次状态发布包含的变化类别，供调用方区分刷新、看门狗武装与过期判定。</summary>
[Flags]
internal enum SmtcCatalogChangeKind
{
    None = 0,
    Status = 1,
    Sessions = 2,
    SessionClosed = 4,
    Focused = 8,
    Playback = 16,
    Metadata = 32,
    Timeline = 64
}

/// <summary>目录的不可变状态；每次变化生成新实例。</summary>
internal sealed record SmtcCatalogState(
    int Generation,
    int Revision,
    SmtcHostStatus Status,
    string? FocusedSessionId,
    IReadOnlyList<SmtcSession> Sessions,
    SmtcCatalogChangeKind LastChange = SmtcCatalogChangeKind.None)
{
    internal static SmtcCatalogState Initial { get; } = new(0, 0, SmtcHostStatus.Starting, null, [], SmtcCatalogChangeKind.None);
}
