using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using DalamudActCompat.ActRuntime;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Mono.Cecil;

internal static unsafe class PostNamazuCommandSmokeTests
{
    private static RaptureShellModule* shell;
    private static nint observedShell, observedUi;
    private static string? observedText;
    private static int calls;

    internal static void Run()
    {
        ValidateNormalCommandUnchanged();
        calls = 0;
        // Substitute only native entry points in this standalone test process.
        // No game process, signatures, UTF8 allocator or real command is invoked.
        var original = ShellCommandModule.Addresses.ExecuteCommandInner.Value;
        var ui = (UIModule*)NativeMemory.AllocZeroed((nuint)sizeof(UIModule));
        var table = (nint*)NativeMemory.AllocZeroed(10, (nuint)sizeof(nint));
        shell = (RaptureShellModule*)NativeMemory.AllocZeroed((nuint)sizeof(RaptureShellModule));
        var allocatedShell = shell;
        try
        {
            *(nint**)ui = table;
            table[9] = (nint)(delegate* unmanaged<UIModule*, RaptureShellModule*>)&GetShell;
            ShellCommandModule.Addresses.ExecuteCommandInner.Value =
                (nint)(delegate* unmanaged<ShellCommandModule*, Utf8String*, UIModule*, void>)&Capture;
            foreach (var text in new[] { "/ [Debug] tmp_type = 无", "/e test", "/splatoon test", "/rotation test" })
            {
                var bytes = Encoding.UTF8.GetBytes(text + "\0");
                fixed (byte* buffer = bytes)
                {
                    var message = default(Utf8String);
                    message.StringPtr = buffer;
                    message.BufUsed = bytes.Length;
                    var before = calls;
                    NativePostNamazuBridge.ExecuteCommandInner(ui, &message);
                    Require(calls == before + 1 && observedText == text && observedShell == (nint)shell && observedUi == (nint)ui,
                        "Native inner entry must receive debug/game/plugin commands unchanged exactly once.");
                }
            }
            shell = null;
            var empty = default(Utf8String);
            var failed = false;
            try { NativePostNamazuBridge.ExecuteCommandInner(ui, &empty); }
            catch (InvalidOperationException ex) { failed = ex.Message.Contains("RaptureShellModule"); }
            Require(failed && calls == 4, "Missing shell must fail without dispatch.");
        }
        finally
        {
            ShellCommandModule.Addresses.ExecuteCommandInner.Value = original;
            NativeMemory.Free(allocatedShell);
            NativeMemory.Free(table);
            NativeMemory.Free(ui);
            shell = null;
        }
        Console.WriteLine("PostNamazu command: native inner ABI, unchanged debug/game/plugin commands and missing-shell guard passed.");
    }

    private static void ValidateNormalCommandUnchanged()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(AppContext.BaseDirectory,
            "BundledActPlugins", "postnamazu", "PostNamazu.dll"));
        var normal = assembly.MainModule.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == "DoNormalTextCommand");
        string Body(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(instruction => instruction.ToString()));
        var before = Body(normal);
        // Apply the real bridge rewrite to the shipped upstream DLL. NormalCommand
        // must retain its native body even when Command is redirected to our bridge.
        typeof(LegacyResourceCompatibility).GetMethod("PatchPostNamazuNativeBridge", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [assembly]);
        Require(Body(normal) == before, "normalcommand native body changed.");
        var command = assembly.MainModule.Types.Single(type => type.FullName == "PostNamazu.Actions.Command")
            .Methods.Single(method => method.Name == "DoTextCommand");
        Require(command.Body.Instructions.Any(instruction => instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == typeof(NativePostNamazuBridge).FullName && called.Name == "SendCommand"),
            "command no longer reaches the semantic bridge.");
        Console.WriteLine("PostNamazu command: shipped DLL rewrite preserves normalcommand's native body.");
    }

    [UnmanagedCallersOnly]
    private static RaptureShellModule* GetShell(UIModule* _) => shell;

    [UnmanagedCallersOnly]
    private static void Capture(ShellCommandModule* self, Utf8String* message, UIModule* ui)
    {
        calls++;
        observedShell = (nint)self;
        observedUi = (nint)ui;
        observedText = message->ToString();
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
