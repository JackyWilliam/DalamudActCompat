using System.Collections;
using System.Reflection;

internal static class MatchaFishingSmokeTests
{
    public static void Run(Assembly assembly, string region)
    {
        var events = new List<object>();
        Action<object> receive = events.Add;
        var handlerType = assembly.GetType("Cafe.Matcha.Network.Handler.FishingHandler", true)!;
        var handler = handlerType.GetConstructors().Single().Invoke([receive]);
        var monitorType = assembly.GetType("Cafe.Matcha.Network.NetworkMonitor", true)!;
        var monitor = Activator.CreateInstance(monitorType)!;
        var handlers = (IList)monitorType.GetField("handlers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
        handlers.Clear();
        handlers.Add(handler);
        var packetType = assembly.GetType("Cafe.Matcha.Network.Packet", true)!;
        var senderType = packetType.GetNestedType("PacketSender")!;
        const uint actor = 0x10000001;
        const long start = 1700000000000L;
        object? Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value);
        object[] Fishing() => events.Where(e => e.GetType().Name == "FishingDTO").ToArray();
        object Last() => Fishing().Last();
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Matcha {region} fishing: {message}");
        }
        byte[] Bytes(ushort opcode, int length, params (int Offset, uint Value)[] fields)
        {
            var bytes = new byte[length];
            BitConverter.GetBytes(actor).CopyTo(bytes, 4);
            BitConverter.GetBytes(actor).CopyTo(bytes, 8);
            BitConverter.GetBytes(opcode).CopyTo(bytes, 18);
            foreach (var (offset, value) in fields) BitConverter.GetBytes(value).CopyTo(bytes, 32 + offset);
            return bytes;
        }
        void Send(byte[] bytes, long timestamp, bool client = false)
            => monitorType.GetMethod(client ? "HandleMessageSent" : "HandleMessageReceived")!
                .Invoke(monitor, ["offline-fishing", timestamp, bytes]);
        byte[] Transition(uint kind, uint tug = 0, bool extended = false)
            => Bytes(extended ? (ushort)0x01C6 : (ushort)0x01FD, extended ? 80 : 72,
                (0, actor), (8, 0x00150001), (12, kind), (28, tug));

        // Exercise the original network entry points, including direction and parser timestamps,
        // while replacing only the callback so no telemetry, toast, or game command is sent.
        Send(Bytes(0x0204, 72, (0, 325), (4, 100)), start - 200);
        Send(Bytes(0x0083, 416, (20, 0x2fb)), start - 150);
        Send(Bytes(0x0187, 44, (0, 0x2bd), (4, 0)), start - 100, client: true);
        Send(Transition(1, extended: true), start);
        Assert((string?)Field(Last(), "Action") == "cast" && (long?)Field(Last(), "CastTime") == start &&
            (uint?)Field(Last(), "BaitId") == 100 && Field(Last(), "Mooch") is false &&
            Field(Last(), "Chum") is true && Field(Last(), "Snagging") is false,
            "cast lost the confirmed command, bait, status snapshot, or network timestamp.");
        var castId = Field(Last(), "CastId");
        var count = events.Count;
        Send(Transition(1, extended: true), start);
        Assert(events.Count == count, "duplicate cast restarted the timer.");
        Send(Bytes(0x00A8, 56, (0, 0x00150001), (4, 1110), (12, 42)), start + 1);
        Assert((uint?)Field(Last(), "PlaceId") == 42, "SystemLogMessage did not update the fishing location.");
        Send(Transition(5, 294), start + 5000);
        Assert((string?)Field(Last(), "Action") == "bite" && (int?)Field(Last(), "Tug") == 3 &&
            (long?)Field(Last(), "BiteTime") == start + 5000, "bite lost tug or time.");
        Assert(events.Count(e => e.GetType().Name == "FishBiteDTO") == 1, "legacy FishBite event was lost.");
        Send(Transition(5, 294), start + 5000);
        Assert(events.Count(e => e.GetType().Name == "FishBiteDTO") == 1, "duplicate bite was emitted.");
        Send(Transition(6), start + 6000);
        Send(Transition(2), start + 6200);
        Send(Bytes(0x0110, 48, (0, 12345)), start + 6500);
        Assert((string?)Field(Last(), "Action") == "catch" && (uint?)Field(Last(), "FishId") == 12345 &&
            Equals(Field(Last(), "CastId"), castId) && (long?)Field(Last(), "HookTime") == start + 6000,
            "hook/reel/catch did not retain the cast identity.");

        count = events.Count;
        Send(Bytes(0x0110, 48, (0, 12345)), start + 6500);
        Send(Transition(4), start + 7000);
        Send(Transition(1)[..60], start + 7100);
        Send(Transition(1), start + 7200, client: true);
        Assert(events.Count == count, "duplicate catch, truncated, or wrong-direction packet emitted an event.");

        // Missing client-side information must remain unknown, not be guessed as ordinary bait.
        Send(Bytes(0x0187, 44, (0, 0x2bd), (4, 0)), start + 7900);
        Send(Transition(1), start + 8000);
        Assert(Field(Last(), "Mooch") is null && Field(Last(), "BaitId") is null,
            "server-direction ClientTrigger was incorrectly accepted.");
        Send(Bytes(0x00A8, 56, (0, 0x00150001), (4, 1121), (12, 777)), start + 8010);
        Assert(Field(Last(), "Mooch") is true && (uint?)Field(Last(), "BaitId") == 777,
            "server-confirmed mooch bait was lost.");
        Send(Transition(4), start + 9000);
        Assert((string?)Field(Last(), "Action") == "end", "ready did not finish the failed cast.");
        Send(Transition(3), start + 9500);
        Assert((string?)Field(Last(), "Action") == "quit", "fishing exit was lost.");
        var reset = Activator.CreateInstance(packetType, Enum.Parse(senderType, "Server"), Bytes(0x032B, 168), start + 10000)!;
        Assert(handlerType.GetMethod("Handle")!.Invoke(handler, [reset]) is false &&
            (string?)Field(Last(), "Action") == "reset", "zone reset consumed the normal zone handler.");
        Console.WriteLine($"Matcha {region}: cast/bite/hook/reel/catch/end/quit/reset, bait/status/mooch, timestamps, duplicates, and direction passed.");
    }
}
