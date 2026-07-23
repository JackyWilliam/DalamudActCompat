using System.Collections.Concurrent;

namespace DalamudActCompat.Overlay;

public sealed class OverlayEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<OverlayEvent>>> handlers = new();

    public IDisposable Subscribe(string eventName, Action<OverlayEvent> handler)
    {
        var list = handlers.GetOrAdd(eventName, static _ => new List<Action<OverlayEvent>>());
        lock (list)
        {
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (list)
            {
                list.Remove(handler);
            }
        });
    }

    public void Publish(OverlayEvent overlayEvent)
    {
        if (!handlers.TryGetValue(overlayEvent.Name, out var list))
        {
            return;
        }

        Action<OverlayEvent>[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }

        foreach (var handler in snapshot)
        {
            handler(overlayEvent);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action dispose;
        private bool isDisposed;

        public Subscription(Action dispose)
        {
            this.dispose = dispose;
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            dispose();
            isDisposed = true;
        }
    }
}

public sealed record OverlayEvent(string Name, object Payload, DateTimeOffset Timestamp);
