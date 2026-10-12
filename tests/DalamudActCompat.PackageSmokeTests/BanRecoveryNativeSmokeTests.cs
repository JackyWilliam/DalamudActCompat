using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class BanRecoveryNativeSmokeTests
{
    internal static unsafe void Run()
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is not { Length: > 0 } library) return;
        // Load the same path that the managed bindings resolve, avoiding two
        // native copies with independent ImGui globals in this test process.
        var nativePath = Path.Combine(AppContext.BaseDirectory, "cimgui.dll");
        File.Copy(library, nativePath, true); NativeLibrary.Load(nativePath);
        DrawFrames();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe void DrawFrames()
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(1000, 900); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 16, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("Ban recovery font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            using var logo = new SkinSmokeTests.EmptyTexture();
            var config = new PluginConfiguration();
            var snapshot = CloudClientSnapshot.SignedOut("请登录或注册账号。");
            var submissions = 0;
            // Match the account harness: draw the actual UI without initializing
            // Dalamud's game-owned window lifecycle in the standalone process.
            var window = (CloudBanNoticeWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CloudBanNoticeWindow));
            void Set(string name, object value) => typeof(CloudBanNoticeWindow).GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, value);
            Set("text", new UiText(config)); Set("logoTexture", logo); Set("headerDrag", new WindowDragController());
            Set("getSnapshot", (Func<CloudClientSnapshot>)(() => snapshot));
            Set("recheckBan", (Action<string, string>)((_, _) => submissions++));
            Set("username", "fixture_user"); Set("password", "");
            var notice = new CloudBanNotice("account_banned", "account", DateTimeOffset.UtcNow.AddDays(-1), null, "测试封禁原因");
            foreach (var language in new[] { "zh-CN", "en" })
            foreach (var scale in new[] { 1f, 1.4f })
            {
                config.UiLanguage = language; io.FontGlobalScale = scale;
                foreach (var lifted in new[] { false, true })
                {
                    Set("notice", notice); Set("lifted", lifted); Set("requiresRestart", true);
                    for (var frame = 0; frame < 3; frame++)
                    {
                        ImGui.NewFrame();
                        using (DactTheme.PushFrame())
                        {
                            ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new Vector2(680, lifted ? 350 : 560));
                            ImGui.Begin("ban-native", ImGuiWindowFlags.NoDecoration);
                            window.Draw();
                            ImGui.End();
                        }
                        ImGui.Render();
                    }
                    if (Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"ban-{language}-{scale * 100:0}-{lifted}.png"));
                    }
                }
            }
            if (submissions != 0) throw new InvalidOperationException("Drawing the ban notice submitted credentials without user input.");
            Console.WriteLine("Ban recovery native UI: Chinese/English, 100/140%, verification and lifted states passed.");
        }
        finally { ImGui.DestroyContext(context); }
    }
}
