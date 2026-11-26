// 测试用空 SMTC 后端；不启动真实 manager，也不访问播放器。
using AFMediaBar.Classes.Services.Media.Smtc;

namespace AFMediaBar.Layout.Tests;

/// <summary>返回空目录的测试后端。/ Test backend that publishes an empty catalog.</summary>
internal sealed class EmptySmtcBackend : ISmtcBackend
{
    public event Action<SmtcBackendEvent>? Changed
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<SmtcBackendSession>?> GetSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SmtcBackendSession>?>([]);

    public Task<SmtcBackendRead?> ReadAsync(string sessionId, SmtcReadScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<SmtcBackendRead?>(null);

    public Task<bool> ExecuteAsync(string sessionId, SmtcCommand command, double? positionSeconds, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<int> GetOsSessionCountAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<string?> GetFocusedSessionIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public void ReleaseSession(string sessionId) { }
}
