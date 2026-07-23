using DalamudActCompat.Parser;

namespace DalamudActCompat.Core.Interfaces;

public interface IParserEngine : IAsyncDisposable
{
    event EventHandler<ParserStatus>? StatusChanged;

    ParserStatus Status { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task RestartAsync(CancellationToken cancellationToken);
}
