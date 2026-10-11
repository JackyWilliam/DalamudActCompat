using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static partial class SkinSmokeTests
{
    // The real picker uses Dalamud's font service. Supply only that service in
    // this standalone native process; no game framework or plugin is started.
    public class ReadyAtlas : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "get_HasBuiltAtlas" ? true : throw new NotSupportedException(method?.Name);
    }

    private static unsafe void FilePicker(NativeUiRasterizer raster, string? output)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var assembly = typeof(FileDialog).Assembly;
        var containerType = assembly.GetType("Dalamud.IoC.Internal.ServiceContainer", true)!;
        var containerService = assembly.GetType("Dalamud.Service`1", true)!.MakeGenericType(containerType);
        var containerField = containerService.GetField("instanceTcs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalContainer = containerField.GetValue(null);
        var containerTcs = Activator.CreateInstance(containerField.FieldType)!;
        containerField.FieldType.GetMethod("SetResult")!.Invoke(containerTcs, [Activator.CreateInstance(containerType)]);
        containerField.SetValue(null, containerTcs);
        var managerType = assembly.GetType("Dalamud.Interface.Internal.InterfaceManager", true)!;
        var service = assembly.GetType("Dalamud.Service`1", true)!.MakeGenericType(managerType);
        var tcsField = service.GetField("instanceTcs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalTcs = tcsField.GetValue(null);
        var manager = RuntimeHelpers.GetUninitializedObject(managerType);
        managerType.GetField("dalamudAtlas", fields)!.SetValue(manager, DispatchProxy.Create<IFontAtlas, ReadyAtlas>());
        var handleType = assembly.GetType("Dalamud.Interface.ManagedFontAtlas.Internals.DelegateFontHandle", true)!;
        managerType.GetField("<IconFontHandle>k__BackingField", fields)!.SetValue(manager, RuntimeHelpers.GetUninitializedObject(handleType));
        var tcs = Activator.CreateInstance(tcsField.FieldType)!;
        tcsField.FieldType.GetMethod("SetResult")!.Invoke(tcs, [manager]); tcsField.SetValue(null, tcs);
        var io = ImGui.GetIO(); var originalFont = io.FontDefault; io.FontDefault = io.Fonts.Fonts[0];
        var fixture = Path.Combine(Path.GetTempPath(), "dact-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "plugins")); File.WriteAllText(Path.Combine(fixture, "sample.dll"), "fixture");
        var config = new PluginConfiguration();
        config.Appearance.UnlockedEasterEggs.UnionWith(SkinCatalog.All.Where(s => s.EasterEgg).Select(s => s.Id));
        var dialogs = new FileDialogManager();
        var raw = ImGui.ColorConvertFloat4ToU32(new Vector4(.188f, .188f, .2f, 1));
        var context = ImGui.GetCurrentContext();
        var cancelRect = Vector4.Zero; var cancelled = false; var inspectFooter = false;
        var dialogField = typeof(FileDialogManager).GetField("dialog", fields)!;
        var footer = typeof(FileDialog).GetMethod("DrawFooter", fields)!;
        void Frame()
        {
            ImGui.NewFrame(); DactTheme.SetCurrent(config.Appearance, true, 3);
            using (DactTheme.PushFrame())
            {
                ImGui.SetNextWindowPos(new(860, 20)); ImGui.SetNextWindowSize(new(230, 160));
                ImGui.Begin("unrelated-picker-sentinel");
                ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(50), raw);
                ImGui.End();
                DactTheme.DrawFileDialog(dialogs);
                if (inspectFooter) DactTheme.DrawFileDialogSurface(() =>
                {
                    // Capture the actual private footer's final Cancel item before
                    // End restores LastItemData to the containing window.
                    ImGui.SetNextWindowPos(new(60, 610)); ImGui.SetNextWindowSize(new(800, 85));
                    ImGui.Begin("actual-picker-footer", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                    var dialog = (FileDialog)dialogField.GetValue(dialogs)!;
                    cancelled |= (bool)footer.Invoke(dialog, null)! && !dialog.GetIsOk();
                    var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
                    cancelRect = new(min.X, min.Y, max.X, max.Y);
                    Check(max.X <= ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X + 1,
                        $"{config.Appearance.SelectedSkin}: native Cancel is clipped by enlarged padding.");
                    ImGui.End();
                });
            }
            ImGui.Render();
        }
        try
        {
            foreach (var skin in SkinCatalog.All)
            {
                config.Appearance.SelectedSkin = skin.Id;
                cancelled = false;
                inspectFooter = false;
                dialogs.OpenFileDialog("选择 ACT 扩展 DLL 或 ZIP", "ACT plugin{.dll,.zip}", (ok, _) => cancelled = !ok, 1, fixture);
                for (var frame = 0; frame < 4; frame++)
                {
                    io.AddMousePosEvent(-100, -100); Frame();
                    var found = false; var sentinel = false; var foundThemedFill = false; var whiteFill = false;
                    for (var i = 0; i < context.Windows.Size; i++)
                    {
                        var w = context.Windows[i]; if (!w.Active) continue;
                        var name = Marshal.PtrToStringUTF8((nint)w.Name) ?? "";
                        if (name == "unrelated-picker-sentinel")
                            for (var v = 0; v < w.DrawList.VtxBuffer.Size; v++) sentinel |= w.DrawList.VtxBuffer[v].Col == raw;
                        if (!name.Contains("OpenFileDialog")) continue;
                        found = true;
                        for (var v = 0; v < w.DrawList.VtxBuffer.Size; v++)
                        {
                            var color = w.DrawList.VtxBuffer[v].Col;
                            whiteFill |= (color & 0x00ffffff) == 0x00ffffff && w.DrawList.VtxBuffer[v].Uv == ImGui.GetFontTexUvWhitePixel();
                            if (skin.Id != SkinCatalog.Default) Check((color & 0x00ffffff) != (raw & 0x00ffffff), $"{skin.Id}: hardcoded gray breadcrumb remains on frame {frame}.");
                            foundThemedFill |= (color & 0x00ffffff) == (ImGui.ColorConvertFloat4ToU32(DactTheme.Palette.Hover) & 0x00ffffff);
                        }
                    }
                    Check(found && sentinel, "Picker was not drawn or changed unrelated UI geometry.");
                    if (skin.Id == SkinCatalog.Eorzea) Check(foundThemedFill, "Eorzea picker did not use its own control palette.");
                    if (skin.Id == SkinCatalog.LiquidGlass) Check(whiteFill, "Glass white surfaces were recolored as file-icon text.");
                }
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"DACT-安装DLL-{skin.Id}.png"));
                inspectFooter = true;
                foreach (var scale in new[] { .75f, 1f, 1.4f })
                {
                    io.FontGlobalScale = scale; cancelled = false; Frame(); Frame();
                    io.AddMousePosEvent((cancelRect.X + cancelRect.Z) / 2, (cancelRect.Y + cancelRect.W) / 2); Frame();
                    io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
                    Check(cancelled, $"{skin.Id}: native Cancel did not activate at {scale}.");
                }
                io.FontGlobalScale = 1;
                inspectFooter = false;
                dialogs.Reset(); Frame();
            }
            Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "File picker leaked themed state.");
            Console.WriteLine("PASS actual Dalamud file picker: all nine skins, repeated frames/reopen, palette fills and unrelated-window isolation.");
        }
        finally
        {
            dialogs.Reset(); tcsField.SetValue(null, originalTcs); containerField.SetValue(null, originalContainer); io.FontDefault = originalFont;
            Directory.Delete(fixture, true); DactTheme.SetCurrent(new(), false, 0);
        }
    }
}
