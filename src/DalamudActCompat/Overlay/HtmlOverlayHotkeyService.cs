using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.Overlay;

internal sealed class HtmlOverlayHotkeyService : IDisposable
{
    private readonly Thread thread;
    private readonly HotkeyWindow window;
    private readonly object lifetime = new();
    private readonly ConcurrentQueue<(int Generation, int Chord)> pending = new();
    private volatile Binding[] bindings = [];
    private int generation;
    private volatile bool suspended;
    private int disposed;

    internal HtmlOverlayHotkeyService()
    {
        var ready = new TaskCompletionSource<HotkeyWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                // A private message pump receives global shortcuts even when all
                // browser windows are hidden, closed, or have never been opened.
                using var control = new HotkeyWindow(chord =>
                {
                    if (pending.Count < 64) pending.Enqueue((Volatile.Read(ref generation), chord));
                });
                _ = control.Handle;
                ready.SetResult(control);
                Application.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "DACT overlay hotkeys" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        window = ready.Task.GetAwaiter().GetResult();
    }

    internal void Configure(IEnumerable<KeyValuePair<string, HtmlOverlayWindowSettings>> overlays)
    {
        var next = overlays.SelectMany(pair => (pair.Value.Hotkeys ?? [])
            .Where(key => key is not null)
            .Select(key => new Binding(pair.Key, key, key.Key | ((int)key.Modifiers << 16), key.Enabled, key.Action))).ToArray();
        lock (lifetime)
        {
            if (disposed != 0) return;
            window.Invoke(() =>
            {
                Interlocked.Increment(ref generation);
                bindings = next;
                RegisterCurrent();
            });
        }
    }

    internal void Suspend(bool value)
    {
        lock (lifetime)
        {
            if (disposed != 0) return;
            window.Invoke(() =>
            {
                if (suspended == value) return;
                suspended = value;
                Interlocked.Increment(ref generation);
                RegisterCurrent();
            });
        }
    }

    private void RegisterCurrent()
    {
        var active = suspended ? [] : bindings.Where(IsValid).Select(x => x.Chord).Distinct().ToArray();
        window.Reconcile(active);
        foreach (var binding in bindings)
            binding.Settings.Status = !binding.Enabled ? OverlayHotkeyStatus.Disabled :
                binding.Settings.Key == 0 ? OverlayHotkeyStatus.Unassigned :
                IsValid(binding) && window.IsRegistered(binding.Chord) && !suspended ? OverlayHotkeyStatus.Registered : OverlayHotkeyStatus.Unavailable;
    }

    private static bool IsValid(Binding binding)
        => binding.Enabled && HtmlOverlayHotkey.IsKeyboardKey(binding.Chord & 0xffff) &&
           (binding.Chord >> 16 & ~15) == 0 && Enum.IsDefined(binding.Action);

    internal void DispatchPending(Action<string, OverlayHotkeyAction> execute)
    {
        // Window messages only enqueue intent. Configuration, browser actions and
        // saves run on Dalamud's framework thread rather than the WinForms thread.
        var current = bindings;
        var expectedGeneration = Volatile.Read(ref generation);
        while (pending.TryDequeue(out var pressed))
        {
            if (Volatile.Read(ref disposed) != 0 || suspended || pressed.Generation != expectedGeneration) continue;
            foreach (var binding in current.Where(x => x.Chord == pressed.Chord && IsValid(x))
                         .DistinctBy(x => (x.Overlay, x.Action)))
                execute(binding.Overlay, binding.Action);
        }
    }

    public void Dispose()
    {
        // Saves may finish on a worker during unload. Serialize the short native
        // registration calls so none invokes a window whose pump has already exited.
        lock (lifetime)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            window.Invoke(() =>
            {
                window.Reconcile([]);
                Application.ExitThread();
            });
            thread.Join();
            pending.Clear();
        }
    }

    private sealed record Binding(string Overlay, HtmlOverlayHotkey Settings, int Chord, bool Enabled, OverlayHotkeyAction Action);

    private sealed class HotkeyWindow(Action<int> pressed) : Control
    {
        private readonly Dictionary<int, int> registered = [];

        internal bool IsRegistered(int chord) => registered.ContainsKey(chord);

        internal void Reconcile(int[] wanted)
        {
            foreach (var old in registered.Keys.Except(wanted).ToArray())
            {
                UnregisterHotKey(Handle, registered[old]);
                registered.Remove(old);
            }
            foreach (var chord in wanted.Except(registered.Keys))
            {
                // MOD_NOREPEAT makes a held shortcut toggle only once. Sharing one
                // OS registration lets the same chord control several overlay instances.
                // IDs are limited to 0xBFFF for applications. Reuse released slots
                // instead of exhausting the range after repeated edits/reloads.
                var id = Enumerable.Range(1, 0xBFFF).First(candidate => !registered.ContainsValue(candidate));
                if (RegisterHotKey(Handle, id, (uint)(chord >> 16) | 0x4000, (uint)(chord & 0xffff)))
                    registered[chord] = id;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                var packed = (int)message.LParam;
                var chord = ((packed >> 16) & 0xffff) | ((packed & 15) << 16);
                foreach (var item in registered)
                    // An old message may outlive a registration that reused its ID.
                    if (item.Value == (int)message.WParam && item.Key == chord) { pressed(item.Key); break; }
            }
            base.WndProc(ref message);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint window, int id);
    }
}
