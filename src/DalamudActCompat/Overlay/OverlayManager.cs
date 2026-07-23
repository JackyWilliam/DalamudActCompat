namespace DalamudActCompat.Overlay;

public sealed class OverlayManager
{
    private readonly OverlayEventBus eventBus;

    public OverlayManager(OverlayEventBus eventBus)
    {
        this.eventBus = eventBus;
    }

    public void PublishCombatData(object payload)
        => eventBus.Publish(new OverlayEvent(OverlayProtocol.CombatData, payload, DateTimeOffset.UtcNow));
}
