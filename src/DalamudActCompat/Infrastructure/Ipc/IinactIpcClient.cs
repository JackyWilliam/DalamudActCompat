using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.Infrastructure.Ipc;

public sealed class IinactIpcClient : IDisposable
{
    private readonly EncounterStateStore stateStore;
    private readonly PluginLogger logger;
    private readonly ICallGateSubscriber<string, bool> createSubscriber;
    private readonly ICallGateSubscriber<string, bool> removeSubscriber;
    private readonly ICallGateSubscriber<JObject, bool> command;
    private readonly ICallGateProvider<JObject, bool> receiver;
    private readonly string subscriberName = $"DalamudActCompat.{Guid.NewGuid():N}";
    private Guid encounterId;
    private DateTimeOffset encounterStart;
    private string encounterTitle = string.Empty;
    private bool registered;
    private bool wasActive;

    public IinactIpcClient(IDalamudPluginInterface pluginInterface, EncounterStateStore stateStore, PluginLogger logger)
    {
        this.stateStore = stateStore;
        this.logger = logger;
        createSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("IINACT.CreateSubscriber");
        removeSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("IINACT.Unsubscribe");
        command = pluginInterface.GetIpcSubscriber<JObject, bool>($"IINACT.IpcProvider.{subscriberName}");
        receiver = pluginInterface.GetIpcProvider<JObject, bool>(subscriberName);
    }

    public bool TryStart(out string? detail)
    {
        if (registered)
        {
            detail = null;
            return true;
        }

        try
        {
            receiver.RegisterFunc(OnEvent);
            if (!createSubscriber.InvokeFunc(subscriberName))
            {
                receiver.UnregisterFunc();
                detail = "IINACT rejected the subscriber.";
                return false;
            }

            command.InvokeAction(new JObject
            {
                ["call"] = "subscribe",
                ["events"] = new JArray("CombatData"),
            });
            registered = true;
            detail = null;
            logger.Information("Connected to IINACT CombatData IPC.");
            return true;
        }
        catch (Exception ex)
        {
            SafeUnregisterReceiver();
            detail = ex.Message;
            logger.Warning($"IINACT IPC is unavailable: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!registered)
        {
            return;
        }

        try
        {
            command.InvokeAction(new JObject
            {
                ["call"] = "unsubscribe",
                ["events"] = new JArray("CombatData"),
            });
            removeSubscriber.InvokeFunc(subscriberName);
        }
        catch (Exception ex)
        {
            logger.Warning($"IINACT IPC cleanup failed: {ex.Message}");
        }
        finally
        {
            registered = false;
            SafeUnregisterReceiver();
        }
    }

    public void Dispose() => Stop();

    private bool OnEvent(JObject message)
    {
        try
        {
            if (!string.Equals(message.Value<string>("type"), "CombatData", StringComparison.Ordinal))
            {
                return true;
            }

            var encounterData = message["Encounter"] as JObject ?? new JObject();
            var title = Text(encounterData, "title", "TITLE", "CurrentZoneName") ?? "Unknown encounter";
            var zone = Text(encounterData, "CurrentZoneName", "title", "TITLE") ?? "Unknown zone";
            var duration = TimeSpan.FromSeconds(Number(encounterData, "DURATION"));
            var now = DateTimeOffset.UtcNow;
            var calculatedStart = now - duration;
            var active = string.Equals(message.Value<string>("isActive"), "true", StringComparison.OrdinalIgnoreCase);
            if (encounterId == Guid.Empty ||
                !string.Equals(encounterTitle, title, StringComparison.Ordinal) ||
                (!wasActive && active) ||
                (encounterStart - calculatedStart).Duration() > TimeSpan.FromSeconds(5))
            {
                encounterId = Guid.NewGuid();
                encounterStart = calculatedStart;
                encounterTitle = title;
            }

            wasActive = active;
            var combatants = MapCombatants(message["Combatant"] as JObject);
            var encounter = new Encounter(
                encounterId,
                encounterStart,
                active ? null : now,
                zone,
                title,
                combatants,
                Array.Empty<DamageEvent>(),
                Array.Empty<HealEvent>(),
                Array.Empty<DeathEvent>(),
                Array.Empty<ActionSummary>(),
                Array.Empty<JobSummary>());
            var snapshot = stateStore.GetSnapshot();
            stateStore.Replace(encounter, snapshot.Recent);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to map IINACT CombatData.");
            return false;
        }
    }

    private static IReadOnlyList<Combatant> MapCombatants(JObject? source)
        => source?.Properties()
            .Where(property => property.Value is JObject)
            .Select(property =>
            {
                var data = (JObject)property.Value;
                var name = Text(data, "name", "NAME") ?? property.Name;
                return new Combatant(
                    name,
                    name,
                    Text(data, "Job", "job") ?? string.Empty,
                    string.Equals(property.Name, "YOU", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "YOU", StringComparison.OrdinalIgnoreCase),
                    Number(data, "damage"),
                    Number(data, "healed"),
                    (int)Math.Min(int.MaxValue, Number(data, "deaths")));
            })
            .ToArray() ?? Array.Empty<Combatant>();

    private static string? Text(JObject data, params string[] names)
        => names.Select(data.Value<string>)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "---");

    private static long Number(JObject data, string name)
    {
        var text = data[name]?.ToString().Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private void SafeUnregisterReceiver()
    {
        try
        {
            receiver.UnregisterFunc();
        }
        catch
        {
            // A provider that was never registered has nothing to clean up.
        }
    }
}
