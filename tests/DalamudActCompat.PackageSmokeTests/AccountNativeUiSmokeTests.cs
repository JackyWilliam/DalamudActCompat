using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class AccountNativeUiSmokeTests
{
    internal static unsafe void Run()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI");
        if (string.IsNullOrEmpty(library)) return;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true); NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DisplaySize = new(1080, 720); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0x024f, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 16, default, ranges);
            Check(io.Fonts.Build(), "Account font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            using var logo = new SkinSmokeTests.EmptyTexture();
            using (var bitmap = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "act-logo.jpg")))
            {
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
                { var c = bitmap.GetPixel(x, y); var i = (y * bitmap.Width + x) * 4; pixels[i] = c.R; pixels[i+1] = c.G; pixels[i+2] = c.B; pixels[i+3] = c.A; }
                raster.AddTexture(logo.Handle, pixels, bitmap.Width, bitmap.Height);
            }
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = (ControlCenterWindow)RuntimeHelpers.GetUninitializedObject(typeof(ControlCenterWindow));
            void Set(string field, object value) => typeof(ControlCenterWindow).GetField(field, hidden)!.SetValue(window, value);
            void Draw(string method, CloudClientSnapshot account) => typeof(ControlCenterWindow).GetMethod(method, hidden)!.Invoke(window, [account]);
            var config = new PluginConfiguration(); var text = new UiText(config);
            var snapshot = CloudClientSnapshot.SignedOut();
            var checks = 0; var logouts = 0; var bindingCodes = 0;
            var logins = new List<CloudLoginRequest>();
            var cloud = new CloudUiBridge(() => snapshot, _ => { }, request => logins.Add(request), _ => { }, () => logouts++, () => { }, _ => { },
                () => { }, _ => { }, _ => { }, () => { }, refreshRecoveryStatus: () => checks++, createDiscordLinkCode: () => bindingCodes++);
            Set("text", text); Set("cloud", cloud); Set("logoTexture", logo); Set("cloudRememberLogin", true);
            foreach (var field in new[] { "cloudUsername", "cloudPassword", "cloudPasswordConfirmation", "cloudActivationKey", "cloudResetCode", "cloudRecoveryKey" }) Set(field, "");
            var page = typeof(ControlCenterWindow).GetNestedType("CloudAuthenticationPage", BindingFlags.NonPublic)!;
            var size = new Vector2(992, 610); var drag = new WindowDragController();
            void Frame()
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(35, 35)); ImGui.SetNextWindowSize(size);
                    ImGui.PushStyleColor(ImGuiCol.WindowBg, DactTheme.Palette.Surface);
                    ImGui.Begin("account-native", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
                    BrandedWindowChrome.Draw(drag, logo, text.Get("主页", "Home"), text.Get("未启动", "Stopped"), DactTheme.Palette.Accent, "0.4.5.8", "account-native");
                    if (!snapshot.IsSignedIn || snapshot.CanUseDact) Set("cloudDiscordCodeRequestedFor", null!);
                    if (ImGui.BeginChild("account-authentication-gate", new(-1, -1), false))
                        Draw(snapshot.IsSignedIn ? "DrawDiscordBindingGate" : "DrawAuthenticationGate", snapshot);
                    ImGui.EndChild(); ImGui.End(); ImGui.PopStyleColor(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render();
            }
            void Save(string name)
            {
                // Let the native navigation animation settle before rasterizing.
                for (var i = 0; i < 24; i++) Frame();
                if (Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT") is { } output)
                { Directory.CreateDirectory(output); raster.Save(ImGui.GetDrawData(), Path.Combine(output, name + ".png")); }
            }
            foreach (var language in new[] { "zh-CN", "en" })
            foreach (var scale in new[] { 1f, 1.4f })
            {
                config.UiLanguage = language; io.FontGlobalScale = scale;
                size = scale == 1 ? new(992, 610) : new(760, 520);
                foreach (var mode in new[] { "Login", "Register", "ResetPassword" })
                {
                    Set("cloudAuthenticationPage", Enum.Parse(page, mode)); Set("cloudResetPasswordStep", false);
                    Save($"{mode}-{language}-{scale * 100:0}");
                    ImGuiWindowPtr hero = default, form = default;
                    for (var i = 0; i < context.Windows.Size; i++)
                    {
                        var w = context.Windows[i];
                        if (!w.Active) continue;
                        var name = Marshal.PtrToStringUTF8((nint)w.Name) ?? "";
                        if (hero.Handle == null && name.Contains("account-authentication-hero")) hero = w;
                        if (form.Handle == null && name.Contains("account-authentication-card")) form = w;
                    }
                    Check(hero.Handle != null && form.Handle != null && form.Pos.X > hero.Pos.X && Math.Abs(form.Pos.Y - hero.Pos.Y) < 2,
                        "Authentication changed to a portrait layout.");
                }
            }
            config.UiLanguage = "zh-CN"; io.FontGlobalScale = 1; size = new(992, 610);
            Set("cloudAuthenticationPage", Enum.Parse(page, "Login"));
            Set("cloudUsername", "keyboard_user"); Set("cloudPassword", "fixture-password");
            Frame(); Frame();
            io.AddMousePosEvent(630, 356); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            io.AddKeyEvent(ImGuiKey.Enter, true); Frame(); io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
            Check(logins.Count == 1 && logins[0].Username == "keyboard_user" && logins[0].Password == "fixture-password",
                "Enter in the password field did not submit exactly one login.");
            Set("cloudUsername", "");
            snapshot = snapshot with { IsSignedIn = true, Username = "binding_user", Recovery = new(true, true, false, 3, 0, true) };
            Save("binding-required"); Check(context.OpenPopupStack.Size == 0, "Binding opened a global modal over the game.");
            Check(bindingCodes == 1, "The binding UI did not prepare exactly one code, or regenerated it every frame.");
            snapshot = snapshot with { DiscordLinkCode = new("native-fixture-code", DateTimeOffset.UtcNow.AddMinutes(10)) };
            Save("binding-ready");
            Check(bindingCodes == 1, "A valid code was replaced while the user was in Discord.");
            io.AddMousePosEvent(5, 5); Frame(); Frame();
            Check(!io.WantCaptureMouse, "DACT captured mouse input outside its window during required binding.");
            io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame();
            Check(!io.WantCaptureMouse && !snapshot.CanUseDact, "An outside click was blocked or bypassed binding.");
            snapshot = snapshot with { DiscordLinkCode = new("native-fixture-code", DateTimeOffset.UtcNow.AddMinutes(-1)) };
            Save("binding-expired");
            Check(bindingCodes == 1, "An expired code caused an automatic generation loop.");
            io.AddKeyEvent(ImGuiKey.Escape, true); Frame(); io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
            Check(context.OpenPopupStack.Size == 0 && !snapshot.CanUseDact, "Escape bypassed binding or introduced a global modal.");
            snapshot = snapshot with { Recovery = new(true, true, true, 3, 1, true) }; Frame(); Frame();
            Check(context.OpenPopupStack.Size == 0, "Server-confirmed binding opened a modal.");
            snapshot = snapshot with { Recovery = null, RecoveryStatusError = "暂时无法连接绑定服务。" }; Save("binding-offline");
            Check(context.OpenPopupStack.Size == 0, "Offline binding blocked the whole game.");
            snapshot = CloudClientSnapshot.SignedOut(); Frame(); Frame();
            Check(context.OpenPopupStack.Size == 0, "Sign-out left a stale binding popup.");
            snapshot = snapshot with { IsSignedIn = true, Username = "binding_user", Recovery = new(true, true, false, 3, 0, true) };
            Frame(); Frame();
            Check(bindingCodes == 2, "A new login could not prepare its own binding code.");
            Console.WriteLine("Native account UI: horizontal login/register/recovery, CN/EN at 100%/140%, window-local binding, outside click passthrough, one-time code preparation and sign-out transitions passed.");
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
