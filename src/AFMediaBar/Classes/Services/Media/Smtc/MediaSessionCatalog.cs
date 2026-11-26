// SMTC 目录门面：对外只发布不可变状态和普通读取结果，不暴露 WinRT/第三方会话对象。
namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>
/// 拥有 SMTC 宿主，并向业务层提供会话缓存、异步读取、命令和看门狗入口。
/// 这里不直接执行 COM；所有 COM 由 <see cref="SmtcSessionHost"/> 串行处理。
/// </summary>
public sealed class MediaSessionCatalog : IDisposable
{
    private readonly SmtcSessionHost _host;
    private int _isDisposed;

    public MediaSessionCatalog()
        : this(new WindowsSmtcBackend())
    {
    }

    internal MediaSessionCatalog(ISmtcBackend backend)
    {
        _host = new SmtcSessionHost(backend);
        _host.StateChanged += OnHostStateChanged;
    }

    /// <summary>最近一次发布的状态；UI 只读取这个不可变快照。/ Latest immutable state published for readers.</summary>
    internal SmtcCatalogState State => _host.State;

    /// <summary>目录是否已就绪；未就绪时消费者使用上一份快照或断开状态。</summary>
    internal bool IsStarted => State.Status == SmtcHostStatus.Ready;

    /// <summary>最近一次启动/重建是否失败；看门狗据此触发重建。</summary>
    internal bool IsFailed => State.Status == SmtcHostStatus.Failed;

    /// <summary>宿主发布新状态；事件在宿主线程触发，订阅者自行切换到 UI。</summary>
    internal event Action<SmtcCatalogState>? StateChanged;

    /// <summary>启动 manager 并枚举已有会话。/ Starts the manager and enumerates existing sessions.</summary>
    internal Task StartAsync(CancellationToken cancellationToken) => _host.StartAsync(cancellationToken);

    /// <summary>重建 manager；旧句柄标记关闭，调用方按当前会话校验丢弃迟到结果。</summary>
    internal Task RestartAsync(CancellationToken cancellationToken) => _host.RestartAsync(cancellationToken);

    /// <summary>重新枚举会话；用于事件丢失后的兜底。</summary>
    internal Task ForceUpdateAsync(CancellationToken cancellationToken) => _host.ForceUpdateAsync(cancellationToken);

    /// <summary>系统当前发布的会话数；-1 表示不可用。</summary>
    internal Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken) => _host.GetOsSessionCountAsync(cancellationToken);

    /// <summary>读取一个会话的普通数据；会话关闭或读取失败时返回 null。</summary>
    internal Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken) =>
        _host.ReadAsync(sessionId, scope, cancellationToken);

    /// <summary>向会话发送媒体命令；所有读取和命令由宿主串行执行。</summary>
    internal Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken) =>
        _host.ExecuteAsync(sessionId, command, positionSeconds, cancellationToken);

    /// <summary>当前焦点会话 ID；只读宿主缓存，不调用 COM。</summary>
    internal string? FocusedSessionId => State.FocusedSessionId;

    /// <summary>按 ID 查找当前会话句柄；只读缓存。</summary>
    internal SmtcSession? FindSession(string sessionId) => _host.Find(sessionId);

    /// <summary>复制当前会话数组；目录未就绪时返回 false，调用方保留上一份快照。</summary>
    internal bool TryGetSnapshot(out SmtcSession[] sessions)
    {
        var state = State;
        if (state.Status != SmtcHostStatus.Ready)
        {
            sessions = [];
            return false;
        }

        sessions = state.Sessions.ToArray();
        return true;
    }

    /// <summary>判断句柄是否仍在当前目录且未关闭。</summary>
    internal bool IsCurrentSession(SmtcSession session) => session.IsOpen && State.Sessions.Contains(session);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;
        _host.StateChanged -= OnHostStateChanged;
        _host.Dispose();
        StateChanged = null;
    }

    private void OnHostStateChanged(SmtcCatalogState state)
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;
        try { StateChanged?.Invoke(state); }
        catch (Exception) { }
    }
}
