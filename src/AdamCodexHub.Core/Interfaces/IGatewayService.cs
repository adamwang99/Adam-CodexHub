namespace AdamCodexHub.Core.Interfaces;

public interface IGatewayService : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    string LocalToken { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
