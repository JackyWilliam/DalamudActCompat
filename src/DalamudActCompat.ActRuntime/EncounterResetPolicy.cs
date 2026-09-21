namespace DalamudActCompat.ActRuntime;

public enum EncounterResetMode { DactDefault, AfterCombat }

public readonly record struct EncounterResetOptions(EncounterResetMode Mode, int Seconds)
{
    public EncounterResetOptions Normalize() => new(Enum.IsDefined(Mode) ? Mode : EncounterResetMode.DactDefault, Math.Clamp(Seconds, 0, 3600));
}

internal sealed class EncounterResetTimer
{
    private readonly object sync = new();
    private EncounterResetOptions options;
    private DateTimeOffset? outSince;
    private bool armed;
    private bool newActivity;

    public void ObserveActivity() { lock (sync) { armed = true; newActivity = true; } }
    public void Reset() { lock (sync) { armed = newActivity = false; outSince = null; } }

    public bool Observe(EncounterResetOptions value, bool inCombat, DateTimeOffset now)
    {
        lock (sync)
        {
            value = value.Normalize();
            if (value != options) { options = value; outSince = null; }
            if (value.Mode == EncounterResetMode.DactDefault || !armed || inCombat)
            {
                outSince = null;
                newActivity = false;
                return false;
            }
            // Damage can arrive before the framework sees combat. Do not reset a
            // newly starting pull just because its combat flag is one frame late.
            if (newActivity)
            {
                outSince = now;
                newActivity = false;
                return false;
            }
            if (outSince is null || now < outSince) outSince = now;
            if (now - outSince < TimeSpan.FromSeconds(value.Seconds)) return false;
            armed = false;
            outSince = null;
            return true;
        }
    }
}
