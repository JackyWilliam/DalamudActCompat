namespace DalamudActCompat.Plugin;

// 开屏动画 setting. Stored as its number, so the order is part of the configuration file:
// FirstOpenPerLaunch must stay 0 because files written before the setting existed read as 0.
public enum SplashAnimationMode
{
    FirstOpenPerLaunch,
    EveryOpen,
    Off,
}
