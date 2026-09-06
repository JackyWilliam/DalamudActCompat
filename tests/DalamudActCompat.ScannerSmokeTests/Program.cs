using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IINACT.Network;

try
{
    ValidateCopyAndDispose();
    ValidateFailureCleanup();
    ValidateOtherHandlesSurviveFinalization();
    ValidateProductionOwnership();
    Console.WriteLine("Scanner resource smoke tests passed: 32,768 event handles survived; copy/rebase, repeated disposal, and failure cleanup passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static void ValidateCopyAndDispose()
{
    using var process = Process.GetCurrentProcess();
    var module = process.MainModule!;
    var copy = MultiSigScanner.CopyModuleImage(module.FileName, module.ModuleMemorySize);
    try
    {
        Check(copy != module.BaseAddress && Marshal.ReadInt16(copy) == 0x5A4D,
            "The independent module copy was not preserved after unmapping its source.");
    }
    finally { Marshal.FreeHGlobal(copy); }

    using var scanner = new MultiSigScanner();
    var liveAddress = scanner.ScanModule("4D 5A");
    Check(liveAddress == scanner.Module.BaseAddress,
        "Scanner results no longer refer to the original live module.");
    scanner.Dispose();
    scanner.Dispose();
    Check(scanner.SearchBase == nint.Zero && Marshal.ReadInt16(liveAddress) == 0x5A4D,
        "Disposing the copy invalidated the live module or retained the allocation.");
}

static void ValidateFailureCleanup()
{
    var invalidImage = Path.Combine(AppContext.BaseDirectory, "Fixtures", "not-an-image.txt");
    var missingImage = invalidImage + ".missing";
    Check(File.Exists(invalidImage) && !File.Exists(missingImage), "Failure fixtures are invalid.");
    using var process = Process.GetCurrentProcess();
    var module = process.MainModule!;
    Action[] failures =
    [
        () => ExpectFailure<Win32Exception>(() => CopyAndFree(missingImage, 4096), "Failed to open"),
        () => ExpectFailure<Win32Exception>(() => CopyAndFree(invalidImage, 4096), "Failed to create"),
        () =>
        {
            using var exclusive = new FileStream(invalidImage, FileMode.Open, FileAccess.Read, FileShare.None);
            ExpectFailure<Win32Exception>(() => CopyAndFree(invalidImage, 4096), "Failed to open");
        },
        () => ExpectFailure<ArgumentOutOfRangeException>(() => CopyAndFree(module.FileName, 0), "moduleSize"),
        () => ValidateLoggingFailure("First 16 bytes", () => CopyAndFree(module.FileName, module.ModuleMemorySize)),
        () => ValidateLoggingFailure("Offset is", () => { using var scanner = new MultiSigScanner(); }),
    ];

    // Warm lazy runtime resources before measuring. No GC is requested during
    // the repeated failures: native handles must close immediately, not later.
    foreach (var failure in failures) failure();
    CollectFinalizers();
    process.Refresh();
    var initialHandleCount = process.HandleCount;
    for (var round = 0; round < 64; round++)
        foreach (var failure in failures) failure();
    process.Refresh();
    Check(process.HandleCount <= initialHandleCount + 4,
        $"Failure paths leaked process handles: {initialHandleCount} -> {process.HandleCount}.");
    Console.WriteLine($"384 failure-path checks: handles {initialHandleCount} -> {process.HandleCount} before forced GC.");
}

static void CopyAndFree(string fileName, int size)
{
    var copy = MultiSigScanner.CopyModuleImage(fileName, size);
    Marshal.FreeHGlobal(copy);
}

static void ValidateLoggingFailure(string stage, Action operation)
{
    IINACT.Plugin.Log.ThrowAt = stage;
    try { ExpectFailure<DiagnosticFailureException>(operation, "injected"); }
    finally { IINACT.Plugin.Log.ThrowAt = null; }
}

static void ValidateOtherHandlesSurviveFinalization()
{
    for (var round = 0; round < 32; round++)
    {
        var noGc = GC.TryStartNoGCRegion(64 * 1024 * 1024);
        MultiSigScanner? scanner = null;
        var events = new List<AutoResetEvent>(1024);
        try
        {
            scanner = new MultiSigScanner();
            for (var index = 0; index < 1024; index++) events.Add(new AutoResetEvent(false));
            scanner.Dispose();
        }
        finally
        {
            if (noGc) GC.EndNoGCRegion();
        }

        CollectFinalizers();
        var invalid = 0;
        try
        {
            foreach (var testEvent in events)
            {
                try
                {
                    Check(testEvent.Set() && testEvent.WaitOne(0), "An event stopped signalling.");
                }
                catch (IOException)
                {
                    invalid++;
                    // In a regression, do not close an already-invalid raw value
                    // a second time while this isolated process is cleaning up.
                    testEvent.SafeWaitHandle.SetHandleAsInvalid();
                }
            }
        }
        finally
        {
            foreach (var testEvent in events) testEvent.Dispose();
            scanner?.Dispose();
        }
        Check(invalid == 0, $"Round {round}: scanner finalizers closed {invalid} unrelated event handles.");
    }
}

static void ValidateProductionOwnership()
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "DalamudActCompat.slnx"))) root = root.Parent;
    Check(root is not null, "Source root was not found.");
    var network = Path.Combine(root!.FullName, "vendor", "IINACT", "IINACT", "Network");
    var source = File.ReadAllText(Path.Combine(network, "MultiSigScanner.cs"));
    var owner = File.ReadAllText(Path.Combine(network, "ZoneDownHookManager.cs"));
    Check(!source.Contains("DangerousGetHandle", StringComparison.Ordinal) &&
          !source.Contains("PInvoke.CloseHandle", StringComparison.Ordinal) &&
          owner.Contains("using var multiScanner = new MultiSigScanner();", StringComparison.Ordinal),
        "The production scanner lost its single ownership or initialization scope.");
}

static void CollectFinalizers()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static void ExpectFailure<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T exception) when (exception.Message.Contains(message, StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{message}'.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class DiagnosticFailureException : Exception
{
    public DiagnosticFailureException() : base("injected diagnostic failure") { }
}

namespace IINACT
{
    internal static class Plugin
    {
        public static DiagnosticLogger Log { get; } = new();
    }

    internal sealed class DiagnosticLogger
    {
        public string? ThrowAt { get; set; }
        public void Debug(string message)
        {
            if (ThrowAt is { } stage && message.Contains(stage, StringComparison.Ordinal))
                throw new DiagnosticFailureException();
        }
    }
}
