using DalamudActCompat.Core.Interfaces;

namespace DalamudActCompat.Parser;

public sealed class ParserEngine : IParserEngine
{
    private readonly IParserEngine inner;

    public ParserEngine(IParserEngine inner)
    {
        this.inner = inner;
        this.inner.StatusChanged += OnInnerStatusChanged;
    }

    public event EventHandler<ParserStatus>? StatusChanged;

    public ParserStatus Status => inner.Status;

    public Task StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken) => inner.RestartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        inner.StatusChanged -= OnInnerStatusChanged;
        await inner.DisposeAsync().ConfigureAwait(false);
    }

    private void OnInnerStatusChanged(object? sender, ParserStatus status)
        => StatusChanged?.Invoke(this, status);
}
