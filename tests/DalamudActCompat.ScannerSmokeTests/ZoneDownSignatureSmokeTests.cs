using IINACT.Network;

internal static class ZoneDownSignatureSmokeTests
{
    internal static unsafe void Run()
    {
        const string current = "2026.10.06.0000.0000";
        // Global 7.57h keeps the 7.57 receive call sites; the exact-version gate once
        // sent it to the pre-7.57 pattern and the parser never started.
        const string hotfix = "2026.10.16.0000.0000";
        const string previous = "2026.09.15.0000.0000";
        // Concrete instruction bytes reproduce the failure without loading or hooking a game.
        byte[][] callSites =
        [
            [0xE8, 1, 2, 3, 4, 0x4C, 0x8B, 0x4E, 0x10, 0x8B, 0x46, 0x1C, 0x45],
            [0xE8, 1, 2, 3, 4, 0x4C, 0x8B, 0x47, 0x10, 0x44, 0x8B, 0x4F, 0x1C, 0x45],
            [0xE8, 1, 2, 3, 4, 0x4C, 0x8B, 0x4F, 0x10, 0x8B, 0x47, 0x1C, 0x45],
        ];
        for (var variant = 0; variant < callSites.Length; variant++)
        {
            var bytes = Enumerable.Repeat((byte)0xCC, 256).ToArray();
            for (var occurrence = 0; occurrence < 3; occurrence++)
                callSites[variant].CopyTo(bytes, 16 + 64 * occurrence);
            fixed (byte* pointer = bytes)
            {
                var start = (nint)pointer;
                nint[] Scan(string signature, int count) => MultiSigScanner.Scan(start, bytes.Length, signature, count);
                string[] accepted = variant == 2 ? [previous] : [current, hotfix];
                string[] rejected = variant == 2 ? [current, hotfix] : [previous];
                foreach (var version in accepted)
                    Check(ZoneDownSignatureResolver.Resolve(version, Scan) == start + 144,
                        $"The zone channel for {version} must be the third complete receive match.");
                foreach (var version in rejected)
                    ExpectMissing(() => ZoneDownSignatureResolver.Resolve(version, Scan));

                // One or two matching calls cannot establish a zone receive hook.
                Array.Fill(bytes, (byte)0xCC, 144, callSites[variant].Length);
                foreach (var version in accepted)
                    ExpectMissing(() => ZoneDownSignatureResolver.Resolve(version, Scan));
            }
        }

        try
        {
            ZoneDownSignatureResolver.Resolve(current, (_, _) => throw new InvalidOperationException("scanner failure"));
            throw new Exception("An unrelated scanner error was swallowed.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "scanner failure") { }
        Console.WriteLine("7.57/7.57h receive signatures: both published variants, old client, channel selection and missing/partial failures passed.");
    }

    private static void ExpectMissing(Action action)
    {
        try { action(); }
        catch (KeyNotFoundException) { return; }
        throw new InvalidOperationException("An incompatible or incomplete receive signature was accepted.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
