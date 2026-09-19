using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class SponsorIdentitySmokeTests
{
    public static unsafe void Run()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI")!;
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(680, 460); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Sponsor font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var admin = AdministratorSmokeTests.LoadIcon(raster);
            var crown = AdministratorSmokeTests.LoadIcon(raster, "SponsorCrown.png", 1000);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            if (output is not null) Directory.CreateDirectory(output);
            foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
            foreach (var skin in new[] { SkinCatalog.Default, SkinCatalog.Eorzea })
            {
                DactTheme.SetCurrent(new() { SelectedSkin = skin }, true, 1);
                io.FontGlobalScale = scale;
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(12)); ImGui.SetNextWindowSize(new(656, 436));
                using (DactTheme.PushFrame())
                {
                    ImGui.Begin("DACT 账号身份预览", ImGuiWindowFlags.NoSavedSettings);
                    AccountIdentityBadge.Text(admin, "普通冒险者", false, DactTheme.Palette.Text, crown, 0);
                    ImGui.Spacing();
                    AccountIdentityBadge.Text(admin, "星海旅人", false, DactTheme.Palette.Text, crown, 1);
                    ImGui.Spacing();
                    AccountIdentityBadge.Text(admin, "绯梦旅人", true, DactTheme.Palette.Text, crown, 10);
                    ImGui.Spacing();
                    var origin = ImGui.GetCursorScreenPos();
                    var list = ImGui.GetWindowDrawList();
                    var before = list.VtxBuffer.Size;
                    AccountIdentityBadge.DrawName(admin, "这个很长的名字需要为皇冠和管理员图标留下位置", true, origin, 260,
                        DactTheme.Palette.Text, crown, 10);
                    Check(Enumerable.Range(before, list.VtxBuffer.Size - before).Any(i => list.VtxBuffer[i].Col == ImGui.GetColorU32(AccountIdentityBadge.SponsorNameColor)),
                        "Sponsor name did not emit the permanent red text color.");
                    // Check the actual texture geometry, including the narrow name:
                    // the crown must precede the administrator icon inside the row.
                    float crownRight = 0, adminLeft = float.MaxValue, adminRight = 0;
                    for (var i = 0; i < list.CmdBuffer.Size; i++)
                    {
                        var command = list.CmdBuffer[i];
                        if (command.TextureId.Handle is not (999 or 1000)) continue;
                        for (var index = (int)command.IdxOffset; index < command.IdxOffset + command.ElemCount; index++)
                        {
                            var vertex = list.VtxBuffer[(int)command.VtxOffset + list.IdxBuffer[index]];
                            if (vertex.Pos.Y < origin.Y || vertex.Pos.Y > origin.Y + ImGui.GetFontSize()) continue;
                            if (command.TextureId.Handle == 1000) crownRight = Math.Max(crownRight, vertex.Pos.X);
                            else { adminLeft = Math.Min(adminLeft, vertex.Pos.X); adminRight = Math.Max(adminRight, vertex.Pos.X); }
                        }
                    }
                    Check(crownRight > origin.X && adminLeft > crownRight && adminRight <= origin.X + 260,
                        "Sponsor/admin order or reserved row width is incorrect.");
                    ImGui.End();
                }
                ImGui.Render();
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"sponsor-badges-{skin}-{scale * 100:0}.png"));
            }

            io.FontGlobalScale = 1;
            double time = 0;
            var confirms = new List<(string, string)>();
            var config = new PluginConfiguration();
            var notice = new CloudRoleNotice(new UiText(config), crown, (name, grant) => confirms.Add((name, grant)),
                () => time, CloudRoleNoticeKind.Sponsor);
            var state = CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Username = "星海旅人", Sponsor = new(1, "first-sponsor", true) };
            void Frame() { ImGui.NewFrame(); notice.Draw(state); ImGui.Render(); }
            Frame(); time = .8; Frame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "sponsor-celebration.png"));
            io.AddMousePosEvent(340, 350); io.AddMouseButtonEvent(0, true); Frame();
            io.AddMouseButtonEvent(0, false); Frame();
            Check(confirms.SequenceEqual([("星海旅人", "first-sponsor")]), "Sponsor confirmation lost the displayed account or grant.");
            state = state with { StatusIsError = true }; Frame();
            Check(notice.IsOpen, "Failed sponsor acknowledgement silently consumed the notice.");
            state = state with { Sponsor = state.Sponsor! with { SponsorNoticePending = false } }; Frame();
            Check(!notice.IsOpen, "Acknowledged sponsorship stayed open.");
            notice.Update(state with { Sponsor = new(1) }); Check(!notice.IsOpen, "An older server without a notice grant started a celebration.");
            state = state with { Sponsor = new(10, "first-sponsor", true), StatusIsError = false };
            config.UiLanguage = "en";
            foreach (var scale in new[] { .75f, 1f, 1.5f, 2f })
            {
                io.FontGlobalScale = scale; time += 1; Frame(); time += 1; Frame();
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"sponsor-celebration-en-{scale * 100:0}.png"));
                var window = context.Windows[context.Windows.Size - 1];
                Check(window.Pos.X >= 0 && window.Pos.Y >= 0 && window.Pos.X + window.Size.X <= io.DisplaySize.X + 1 &&
                      window.Pos.Y + window.Size.Y <= io.DisplaySize.Y + 1, "Sponsor notice escaped the viewport.");
            }
            notice.Update(CloudClientSnapshot.SignedOut()); Check(!notice.IsOpen, "Logout left another account's sponsor celebration visible.");
            notice.Update(state with { Sponsor = new() }); Check(!notice.IsOpen, "Revocation left the sponsor celebration visible.");
            Console.WriteLine("Sponsor identity: real crown/admin order, permanent red names, clipped long names, two skins/four scales, native first-notice click/retry/ack/legacy/logout/revoke and English viewport checks passed.");
        }
        finally { DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
