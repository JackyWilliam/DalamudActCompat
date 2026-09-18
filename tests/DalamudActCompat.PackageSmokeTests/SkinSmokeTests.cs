using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using Newtonsoft.Json;

internal static class SkinSmokeTests
{
    public static async Task ApiAsync(string root)
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var old = System.Text.Json.JsonSerializer.Deserialize<CloudAccountStatus>("""{"user":{"username":"legacy"},"administrator":{"isAdmin":true}}""", json)!;
        Check(old.Sponsor is null, "Legacy/admin response granted sponsorship.");
        using var data = System.Text.Json.JsonDocument.Parse(Environment.GetEnvironmentVariable("DACT_SPONSOR_FIXTURE")!);
        var user = data.RootElement.GetProperty("user");
        var other = data.RootElement.GetProperty("other");
        var paths = new PluginPaths(Path.Combine(root, "skins-api")); paths.EnsureCreated();
        var disk = new CloudCredentialStore(paths.CloudCredentialFile);
        CloudStoredCredentials Credentials(System.Text.Json.JsonElement value) => new(value.GetProperty("username").GetString()!, value.GetProperty("token").GetString()!,
            DateTimeOffset.UtcNow.AddDays(1), new PortableConfigurationBackupService().GenerateRecoveryKey());
        var sponsor = Credentials(user);
        disk.Save(sponsor);
        using var http = new HttpClient { BaseAddress = new(data.RootElement.GetProperty("baseUrl").GetString()!) };
        using var api = new CloudApiClient(http);
        CloudClientService Service() => new(paths, api, disk, new(paths.CloudBanFile), new(paths.CloudDeviceFile), new CloudKeyEnvelopeService(), new PortableConfigurationBackupService());
        using (var service = Service())
        {
            await service.InitializeAsync(default);
            Check(service.Snapshot is { IsSignedIn: true, Sponsor.Tier: 1 }, "Real login did not fetch sponsorship.");
            var response = await api.GetAccountStatusAsync(sponsor.Token, default);
            Check(response.Sponsor?.Tier == 1, "Heartbeat account response lost sponsorship.");
        }
        using (var cold = Service())
        {
            await cold.InitializeAsync(default);
            Check(cold.Snapshot.Sponsor?.Tier == 1, "Cold login lost a permanent sponsor grant.");
        }
        disk.Save(Credentials(other));
        using (var standard = Service())
        {
            await standard.InitializeAsync(default);
            Check(standard.Snapshot is { IsSignedIn: true, Sponsor.Tier: 0 }, "A different account inherited sponsorship.");
            var apply = typeof(CloudClientService).GetMethod("ApplyAccountStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            apply.Invoke(standard, [sponsor, new CloudAdministratorStatus(), null, new CloudSponsorStatus(1)]);
            Check(standard.Snapshot.Sponsor?.Tier == 0, "A late response from the previous account granted a skin.");
            await standard.LogoutAsync(default);
            Check(!standard.Snapshot.IsSignedIn && standard.Snapshot.Sponsor is null, "Logout retained sponsor authority.");
        }
        Console.WriteLine("Sponsor HTTP: real server/client cold login, heartbeat, account isolation, stale response and logout passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run(bool native = false)
    {
        var config = JsonConvert.DeserializeObject<PluginConfiguration>("""{"Version":16,"Appearance":{"SelectedSkin":"eorzea","SponsorTier":10,"UnlockedEasterEggs":["eorzea","bogus"]},"Meter":{"HorizontalWindow":{"BackgroundOpacity":0.85}}}""")!;
        config.ApplyMigrations();
        Check(config.Version == 17 && config.Meter.HorizontalWindow.BackgroundOpacity == 0, "Migration changed the old horizontal appearance.");
        Check(config.Appearance.UnlockedEasterEggs.Count == 0 && SkinCatalog.Resolve(config.Appearance, true, 0) == SkinCatalog.Default,
            "A local config or discovery list granted sponsor access.");
        Check(SkinCatalog.Resolve(config.Appearance, false, 1) == SkinCatalog.Default && SkinCatalog.Resolve(config.Appearance, true, 1) == SkinCatalog.Eorzea,
            "Sponsor authorization ignored the active account.");
        Check(config.Appearance.SelectedSkin == SkinCatalog.Eorzea, "Temporary loss of access destroyed the preferred skin.");
        var discoveries = new SkinDiscoveries();
        for (var i = 0; i < 9; i++) Check(discoveries.ClickLogo(config.Appearance, i * 100) is null, "Logo discovery unlocked early.");
        Check(discoveries.ClickLogo(config.Appearance, 900) == SkinCatalog.Jade && discoveries.ClickLogo(config.Appearance, 1000) is null,
            "Tenth logo click did not unlock exactly once.");
        for (var i = 0; i < 6; i++) discoveries.ClickVersion(config.Appearance, i * 100);
        Check(discoveries.ClickVersion(config.Appearance, 6000) is null, "Long idle time counted as a click sequence.");
        for (var i = 1; i < 6; i++) discoveries.ClickVersion(config.Appearance, 6000 + i * 100);
        Check(discoveries.ClickVersion(config.Appearance, 6600) == SkinCatalog.Amethyst, "Version discovery did not unlock.");
        for (var i = 0; i < 5; i++) Check(discoveries.VisitPage(config.Appearance, i) is null, "Exploration unlocked before visiting all pages.");
        Check(discoveries.VisitPage(config.Appearance, 5) == SkinCatalog.Amber && discoveries.VisitPage(config.Appearance, 0) is null,
            "Page discovery repeated or missed the sixth page.");
        config.Appearance.SelectedSkin = SkinCatalog.Jade;
        config.Meter.HorizontalWindow.BackgroundColor = new(.12f, .23f, .34f);
        config.Meter.HorizontalWindow.BackgroundOpacity = .7f;
        config.Meter.RoleSplitDamageWindow.BackgroundColor = new(.3f, .2f, .1f);
        config.Meter.RoleSplitHealerWindow.BackgroundColor = new(.1f, .2f, .3f);
        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(JsonConvert.SerializeObject(config))!;
        restored.ApplyMigrations();
        Check(restored.Meter.HorizontalWindow.BackgroundOpacity == .7f && restored.Meter.HorizontalWindow.BackgroundColor == config.Meter.HorizontalWindow.BackgroundColor,
            "Cold reload or repeat migration lost the horizontal color/opacity.");
        Check(restored.Meter.RoleSplitDamageWindow.BackgroundColor != restored.Meter.RoleSplitHealerWindow.BackgroundColor,
            "D/T and H backgrounds were coupled.");
        Check(restored.Appearance.UnlockedEasterEggs.SetEquals([SkinCatalog.Jade, SkinCatalog.Amber, SkinCatalog.Amethyst]) &&
              SkinCatalog.Resolve(restored.Appearance, true, 0) == SkinCatalog.Jade, "Discovered colors did not persist or incorrectly require sponsorship.");
        var background = restored.Meter.HorizontalWindow;
        var fill = MeterBackground.Fill(background);
        Check(Math.Abs(fill.W - .7f) < .00001f && new Vector3(fill.X, fill.Y, fill.Z) == background.BackgroundColor,
            "Background color changed or opacity was multiplied twice.");
        background.BackgroundColor = new(float.NaN, -1, 8);
        background.Normalize(MeterSlotDefaults.CreateHorizontal());
        Check(background.BackgroundColor == new Vector3(MeterBackground.DefaultColor.X, 0, 1), "Invalid background channels reached the renderer.");
        config.ResetToDefaults("logs");
        Check(config.Appearance.SelectedSkin == SkinCatalog.Default && config.Appearance.UnlockedEasterEggs.Count == 0, "Factory reset retained skin preferences.");
        if (native) Native();
        Console.WriteLine("Skins: permissions, discoveries, migration, serialization, independent backgrounds and requested native checks passed.");
    }

    internal static void LoadGameTextures(NativeUiRasterizer raster)
    {
        if (Environment.GetEnvironmentVariable("DACT_TEST_GAME_SQPACK") is not { Length: > 0 } sqpack) return;
        using var game = new Lumina.GameData(sqpack);
        var assets = new Dictionary<string, IDalamudTextureWrap>();
        foreach (var name in GameSkinAssets.Files)
        {
            var texture = game.GetFile<Lumina.Data.Files.TexFile>(GameSkinAssets.PathFor(name, true));
            Check(texture is not null, $"Original Light theme asset is missing: {name}");
            var rgba = texture!.ImageData.ToArray();
            // Lumina returns BGRA; the software ImGui renderer uses RGBA.
            for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            var wrap = new EmptyTexture(2000 + assets.Count, texture.Header.Width, texture.Header.Height);
            assets.Add(name, wrap);
            raster.AddTexture(wrap.Handle, rgba, wrap.Width, wrap.Height);
        }
        DactTheme.GameAssets = new GameSkinAssets(name => assets.GetValueOrDefault(name));
    }

    private static unsafe void Native()
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") ?? throw new Exception("Set DACT_TEST_CIMGUI.");
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(1120, 840); io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[] { 0x20, 0xff, 0x2000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0 };
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"), 17, default, ranges);
            Check(io.Fonts.Build(), "Skin font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            LoadGameTextures(raster);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            if (output is not null) Directory.CreateDirectory(output);
            var config = new PluginConfiguration();
            config.Appearance.UnlockedEasterEggs.UnionWith([SkinCatalog.Jade, SkinCatalog.Amethyst, SkinCatalog.Amber]);
            var account = CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Username = "preview-sponsor", Sponsor = new(1) };
            var text = new UiText(config); var logo = new EmptyTexture(); var drag = new WindowDragController();
            using (var bitmap = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "act-logo.jpg")))
            {
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y); var offset = (y * bitmap.Width + x) * 4;
                    pixels[offset] = pixel.R; pixels[offset + 1] = pixel.G; pixels[offset + 2] = pixel.B; pixels[offset + 3] = pixel.A;
                }
                raster.AddTexture(logo.Handle, pixels, bitmap.Width, bitmap.Height);
            }
            var discoveries = new SkinDiscoveries(); var unlockCount = 0; var closed = false;
            var skins = new SkinSettingsPanel(); var saves = 0;
            var windowSize = new Vector2(1060, 780);
            var closeButtonPosition = Vector2.Zero;
            skins.Open(config.Appearance, account);
            void Frame()
            {
                DactTheme.SetCurrent(config.Appearance, account.IsSignedIn, account.Sponsor?.Tier ?? 0);
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ControlCenterWindow.PushTheme();
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(windowSize);
                    ImGui.PushStyleColor(ImGuiCol.WindowBg, DactTheme.Palette.Surface);
                    ImGui.Begin("skin-components", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings));
                    closed |= BrandedWindowChrome.Draw(drag, logo, "设置&账号", "运行中", DactTheme.Palette.Accent, "0.4.3.1", "skin-test",
                        helpAction: () => { }, friendsAction: () => { }, onlineFriends: 2,
                        logoAction: () => { if (discoveries.ClickLogo(config.Appearance, Environment.TickCount64) is not null) unlockCount++; });
                    BrandedWindowChrome.DrawNavigationRail("skin-preview-tabs", ["概览", "战斗统计", "悬浮窗", "扩展", "云同步", "设置&账号"], 5);
                    ImGui.BeginChild("control-center-page-content", new Vector2(-1, ControlCenterWindow.PageContentHeight()), true,
                        (skins.IsOpen ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None) |
                        (DactTheme.Palette.Light ? ImGuiWindowFlags.NoBackground : ImGuiWindowFlags.None));
                    if (skins.IsOpen)
                    {
                        if (skins.Draw(config.Appearance, account, text, () => { }, () => closed = true)) saves++;
                        closeButtonPosition = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
                    }
                    else skins.DrawEntry(config.Appearance, account, text);
                    ImGui.EndChild();
                    ImGui.End(); ImGui.PopStyleColor(); ControlCenterWindow.PopTheme();
                }
                ImGui.Render();
                Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Skin leaked ImGui style state.");
            }
            foreach (var skin in SkinCatalog.All)
            {
                config.Appearance.SelectedSkin = skin.Id; skins.Open(config.Appearance, account); Frame(); Frame();
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"skin-{skin.Id}.png"));
            }
            void Click(Vector2 position)
            {
                io.AddMousePosEvent(position.X, position.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
            }
            Vector2 CardPosition(int index)
            {
                for (var i = 0; i < context.Windows.Size; i++)
                {
                    var window = context.Windows[i];
                    if (!(Marshal.PtrToStringUTF8((nint)window.Name) ?? "").Contains("/skin-catalogue")) continue;
                    return window.Pos + window.WindowPadding + new Vector2(40,
                        ImGui.GetTextLineHeightWithSpacing() + index * (108 + ImGui.GetStyle().ItemSpacing.Y) + 30);
                }
                throw new Exception("Skin catalogue was not rendered.");
            }
            // Actual clicks verify that browsing and leaving cannot commit a
            // preference. Applying returns exactly one save to the owning page.
            config.Appearance.SelectedSkin = SkinCatalog.Default;
            skins.Open(config.Appearance, account); Frame(); Frame();
            Click(CardPosition(1));
            Check(skins.PreviewSkinId == SkinCatalog.Eorzea && config.Appearance.SelectedSkin == SkinCatalog.Default && saves == 0,
                "Preview changed the saved skin.");
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "skin-browser-preview-only.png"));
            Click(closeButtonPosition - new Vector2(158, 0)); Frame();
            Check(config.Appearance.SelectedSkin == SkinCatalog.Eorzea && saves == 1, "Apply did not save exactly once.");
            Click(CardPosition(0));
            Click(closeButtonPosition - new Vector2(316, 0));
            Check(!skins.IsOpen && !closed && config.Appearance.SelectedSkin == SkinCatalog.Eorzea && saves == 1,
                "Back applied a draft or closed the main window.");
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "skin-settings-entry.png"));
            skins.Open(config.Appearance, account); Frame(); Frame(); Click(CardPosition(0));
            io.AddKeyEvent(ImGuiKey.Escape, true); Frame(); io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
            Check(!skins.IsOpen && !closed && saves == 1, "Escape did not return without saving.");
            skins.Open(config.Appearance, account); Frame(); Frame(); Click(CardPosition(0));
            Click(closeButtonPosition);
            Check(closed && !skins.IsOpen && config.Appearance.SelectedSkin == SkinCatalog.Eorzea && saves == 1,
                "Close applied a draft or failed to close.");
            closed = false;
            account = account with { Sponsor = new(0) };
            config.Appearance.SelectedSkin = SkinCatalog.Default;
            skins.Open(config.Appearance, account); Frame(); Frame(); Click(CardPosition(1));
            Click(closeButtonPosition - new Vector2(158, 0));
            Check(config.Appearance.SelectedSkin == SkinCatalog.Default && saves == 1, "Locked sponsor preview granted a skin.");
            config.Appearance.UnlockedEasterEggs.Clear(); Frame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "skin-browser-locked.png"));
            account = account with { Sponsor = new(1) };
            config.Appearance.SelectedSkin = SkinCatalog.Eorzea;
            foreach (var language in new[] { "zh-CN", "en" })
            foreach (var scale in new[] { 1f, 1.4f })
            {
                config.UiLanguage = language; io.FontGlobalScale = scale; windowSize = new(760, 520);
                skins.Open(config.Appearance, account); Frame(); Frame();
                Check(closeButtonPosition.X < 790 && closeButtonPosition.Y < 550, "Skin footer escaped a small window.");
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"skin-browser-small-{language}-{scale * 100:0}.png"));
                Click(closeButtonPosition);
                Check(closed && !skins.IsOpen, "Close is unreachable with a large font or small window."); closed = false;
            }
            config.UiLanguage = "zh-CN"; io.FontGlobalScale = 1; windowSize = new(1060, 780);
            skins.Open(config.Appearance, account); Frame(); Frame();
            config.Appearance.SelectedSkin = SkinCatalog.Eorzea;
            account = account with { Sponsor = new(0) }; Frame();
            Check(DactTheme.CurrentSkin == SkinCatalog.Default, "Revocation left the sponsor theme active.");
            // Feed real cimgui hit targets; this sends no input to Windows or the game.
            config.Appearance.UnlockedEasterEggs.Remove(SkinCatalog.Jade); Frame();
            for (var i = 0; i < 10; i++)
            {
                io.AddMousePosEvent(60, 58); Frame(); io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
            }
            Check(unlockCount == 1 && !closed, "Logo hit target dragged/closed the window or missed discovery clicks.");
            ButtonAlignment(raster, output);
            PopupSurfaces(raster, output, config, text, logo);
            MeterEditor(raster, output, logo, config, text);
        }
        finally { DactTheme.GameAssets = null; DactTheme.SetCurrent(new(), false, 0); ImGui.DestroyContext(context); }
    }

    private static void ButtonAlignment(NativeUiRasterizer raster, string? output)
    {
        DactTheme.SetCurrent(new() { SelectedSkin = SkinCatalog.Eorzea }, true, 1);
        var io = ImGui.GetIO();
        foreach (var scale in new[] { .85f, 1f, 1.4f })
        {
            io.FontGlobalScale = scale;
            for (var frame = 0; frame < 2; frame++)
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(960, 420));
                    ImGui.Begin("button-alignment", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration));
                    DactTheme.DrawGameWindow();
                    ImGui.TextUnformatted($"原版按钮 · 文字居中验证 · {scale:P0}");
                    foreach (var label in new[] { "保存", "打开配置", "Apply", "Settings", "配置 Settings" })
                    {
                        for (var state = 0; state < 3; state++)
                        {
                            if (state > 0) ImGui.SameLine();
                            ImGui.PushID(state);
                            ImGui.BeginDisabled(state == 2);
                            DactTheme.Button(label, new((state == 0 ? 140 : 180) * scale, 28 * scale));
                            ImGui.EndDisabled(); ImGui.PopID();
                        }
                    }
                    ImGui.TextDisabled("同一文字在不同宽度与禁用状态下，按字形可见边界居中。");
                    ImGui.End();
                }
                ImGui.Render();
            }
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"button-alignment-{scale * 100:0}.png"));
        }
        io.FontGlobalScale = 1;
    }

    private static unsafe void PopupSurfaces(NativeUiRasterizer raster, string? output, PluginConfiguration config, UiText text, EmptyTexture logo)
    {
        // Call the actual popup renderer without starting parsers or live cloud work.
        var center = (ControlCenterWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ControlCenterWindow));
        void Field(string name, object value) => typeof(ControlCenterWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(center, value);
        var snapshot = CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Username = "preview-user", StatusMessage = "已刷新，共 2 个云端版本。",
            Backups = [new("preview", DateTimeOffset.Now, 1234, "preview")] };
        Field("configuration", config); Field("text", text); Field("saveConfiguration", (Action)(() => { }));
        Field("cloud", new CloudUiBridge(() => snapshot, _ => { }, _ => { }, _ => { }, () => { }, () => { }, _ => { }, () => { }, _ => { }, _ => { }, () => { }));
        var popup = typeof(ControlCenterWindow).GetMethod("DrawCloudQuickPopup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var context = ImGui.GetCurrentContext(); var io = ImGui.GetIO();
        var comboPosition = Vector2.Zero; var drag = new WindowDragController(); var selected = 0;
        string[] items = ["文字提醒 + 时间轴", "DPS Rdmty", "DPS Xephero", "Eureka", "Jobs", "OopsyRaidsy", "PullCounter", "Test"];
        void Frame()
        {
            ImGui.NewFrame();
            using (DactTheme.PushFrame())
            {
                ControlCenterWindow.PushTheme();
                ImGui.SetNextWindowPos(new(30, 30)); ImGui.SetNextWindowSize(new(1060, 760));
                ImGui.Begin("popup-surfaces", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration));
                BrandedWindowChrome.Draw(drag, logo, "主页", "运行中", DactTheme.Palette.Accent, "0.4.3.1", "popup-test");
                ImGui.SetNextItemWidth(500);
                DactTheme.Combo("本地模板", ref selected, items, items.Length);
                comboPosition = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
                popup.Invoke(center, [snapshot]);
                ImGui.End(); ControlCenterWindow.PopTheme();
            }
            ImGui.Render();
            Check(context.ColorStack.Size == 0 && context.StyleVarStack.Size == 0, "Popup leaked style state.");
        }
        void Click(Vector2 at) { io.AddMousePosEvent(at.X, at.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame(); }
        foreach (var scale in new[] { 1f, 1.4f })
        {
            io.FontGlobalScale = scale;
            DactTheme.SetCurrent(new() { SelectedSkin = SkinCatalog.Eorzea }, true, 1);
            Field("cloudQuickPopupRequested", true); Frame(); Frame();
            var window = new ImGuiWindowPtr(context.OpenPopupStack[0].Window);
            Check(window.Pos.X >= 8 && window.Pos.Y >= 8 && window.Pos.X + window.Size.X <= io.DisplaySize.X - 8 &&
                window.Pos.Y + window.Size.Y <= io.DisplaySize.Y - 8, "Cloud popup escaped the viewport.");
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"cloud-popup-metal-{scale * 100:0}.png"));
            Click(window.Pos + new Vector2(window.Size.X - window.WindowPadding.X - 12 * scale, window.WindowPadding.Y + 12 * scale));
            Check(context.OpenPopupStack.Size == 0, "Cloud popup close button did not dismiss it.");
            Field("cloudQuickPopupRequested", true); Frame(); Frame();
            io.AddKeyEvent(ImGuiKey.Escape, true); Frame(); io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
            Check(context.OpenPopupStack.Size == 0, "Cloud popup Escape did not dismiss it.");
            Click(comboPosition); Frame();
            Check(context.OpenPopupStack.Size > 0, "Themed combo did not open.");
            window = new ImGuiWindowPtr(context.OpenPopupStack[0].Window);
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"dropdown-metal-{scale * 100:0}.png"));
            Click(window.Pos + window.WindowPadding + new Vector2(50, ImGui.GetTextLineHeightWithSpacing() * 1.5f));
            Check(selected == 1 && context.OpenPopupStack.Size == 0, "Themed combo lost selection or dismissal behavior.");
            selected = 0;
        }
        io.FontGlobalScale = 1;
    }

    private static unsafe void MeterEditor(NativeUiRasterizer raster, string? output, EmptyTexture logo, PluginConfiguration config, UiText text)
    {
        var meter = new MeterService(new EncounterStateStore(), config.Meter);
        var icons = new JobIconTextureSet(null!, Path.Combine(Path.GetTempPath(), "dact-missing-preview-icons"));
        var classic = new MeterWindow(meter, null!, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
        var horizontal = new HorizontalMeterWindow(meter, config, text, icons, () => { });
        var dt = new RoleSplitMeterWindow(meter, config, text, classic, () => { }, RoleSplitGroup.DamageTank);
        var h = new RoleSplitMeterWindow(meter, config, text, classic, () => { }, RoleSplitGroup.Healer);
        var saves = 0;
        var editor = new MeterStyleEditorWindow(config, logo, classic, horizontal, dt, h, text, () => saves++);
        foreach (var profile in new[] { config.Meter.ClassicWindow, config.Meter.HorizontalWindow,
                     config.Meter.RoleSplitDamageWindow, config.Meter.RoleSplitHealerWindow })
            profile.Slots.Add(new() { Metric = MeterSlotMetric.TeamDps, Visible = true });
        config.Meter.ActivateWindow(MeterWindowKind.Horizontal);
        config.Meter.HorizontalWindow.BackgroundColor = new(.09f, .19f, .26f);
        config.Meter.HorizontalWindow.BackgroundOpacity = .88f;
        editor.Open();
        for (var i = 0; i < 3; i++)
        {
            ImGui.NewFrame(); editor.PreDraw();
            ImGui.SetNextWindowPos(new(25, 35)); ImGui.SetNextWindowSize(new(1070, 760));
            ImGui.Begin("native-meter-editor", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration)); editor.Draw(); ImGui.End(); editor.PostDraw(); ImGui.Render();
        }
        if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "meter-background-editor.png"));
        foreach (var kind in new[] { MeterWindowKind.Horizontal, MeterWindowKind.Classic, MeterWindowKind.RoleSplit })
        {
            config.Meter.ActivateWindow(kind);
            foreach (var profile in new[] { config.Meter.HorizontalWindow, config.Meter.ClassicWindow, config.Meter.RoleSplitDamageWindow, config.Meter.RoleSplitHealerWindow })
            {
                profile.BackgroundColor = new(.91f, .87f, .76f);
                profile.BackgroundOpacity = 1;
            }
            config.Appearance.SelectedSkin = SkinCatalog.Eorzea;
            DactTheme.SetCurrent(config.Appearance, true, 1);
            editor.Open();
            for (var i = 0; i < 3; i++)
            {
                ImGui.NewFrame();
                using (DactTheme.PushFrame())
                {
                    editor.PreDraw(); ImGui.SetNextWindowPos(new(25, 35)); ImGui.SetNextWindowSize(new(1070, 760));
                    ImGui.Begin("native-meter-editor", DactTheme.WindowFlags(ImGuiWindowFlags.NoDecoration)); editor.Draw(); ImGui.End(); editor.PostDraw();
                }
                ImGui.Render();
            }
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-light-{kind}.png"));
        }
        config.Meter.ActivateWindow(MeterWindowKind.Horizontal);
        editor.Open();
        var before = config.Meter.HorizontalWindow.BackgroundColor;
        config.Meter.HorizontalWindow.BackgroundColor = new(.8f, .2f, .1f);
        typeof(MeterStyleEditorWindow).GetMethod("RestoreEditingSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
        Check(config.Meter.HorizontalWindow.BackgroundColor == before, "Editor Cancel lost the original background.");
        editor.Open(); config.Meter.HorizontalWindow.BackgroundColor = new(.21f, .15f, .31f);
        typeof(MeterStyleEditorWindow).GetMethod("SaveAndClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
        Check(config.Meter.HorizontalWindow.BackgroundColor == new Vector3(.21f, .15f, .31f), "Editor Save reverted the selected background.");
        var io = ImGui.GetIO(); var context = ImGui.GetCurrentContext();
        var cancelPosition = Vector2.Zero;
        var outerScroll = 0f;
        void Frame()
        {
            ImGui.NewFrame();
            using (DactTheme.PushFrame())
            {
                editor.PreDraw(); ImGui.SetNextWindowPos(new(25, 35)); ImGui.SetNextWindowSize(new(880, 590));
                ImGui.Begin("native-meter-editor", editor.Flags);
                editor.Draw();
                var rectangleMin = ImGui.GetItemRectMin(); var rectangleMax = ImGui.GetItemRectMax();
                cancelPosition = (rectangleMin + rectangleMax) * .5f;
                Check(rectangleMax.Y <= ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y - 12 && ImGui.IsItemVisible(),
                    "Editor actions overlap the metal border or leave the visible content area.");
                outerScroll = ImGui.GetScrollMaxY();
                ImGui.End(); editor.PostDraw();
            }
            ImGui.Render();
        }
        void Click(Vector2 at) { io.AddMousePosEvent(at.X, at.Y); Frame(); io.AddMouseButtonEvent(0, true); Frame(); io.AddMouseButtonEvent(0, false); Frame(); }
        foreach (var kind in Enum.GetValues<MeterWindowKind>())
        foreach (var scale in new[] { .85f, 1f, 1.4f, 2f })
        {
            io.FontGlobalScale = scale; config.Meter.ActivateWindow(kind); editor.Open(); Frame(); Frame();
            Check(outerScroll == 0, $"Editor footer created an outer scroll range: {kind}, {scale}, {outerScroll}.");
            if (output is not null && scale is 1f or 1.4f) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"meter-footer-{kind}-{scale * 100:0}.png"));
            var savedBefore = saves;
            Click(cancelPosition - new Vector2(118, 0));
            Check(!editor.IsOpen && saves == savedBefore + 1, "Visible footer Save was not clickable or did not commit once.");
            editor.Open(); Frame(); Frame(); Click(cancelPosition);
            Check(!editor.IsOpen && context.OpenPopupStack.Size == 0, "Clean editor Cancel did not close.");
        }
        io.FontGlobalScale = 1;
    }

    private sealed class EmptyTexture(int handle = 998, int width = 1, int height = 1) : ISharedImmediateTexture, IDalamudTextureWrap
    {
        public ImTextureID Handle => new(handle);
        public int Width => width;
        public int Height => height;
        public Vector2 Size => new(Width, Height);
        public IDalamudTextureWrap GetWrapOrEmpty() => this;
        public IDalamudTextureWrap GetWrapOrDefault(IDalamudTextureWrap? fallback = null) => this;
        public bool TryGetWrap([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDalamudTextureWrap? wrap, out Exception? exception) { wrap = this; exception = null; return true; }
        public Task<IDalamudTextureWrap> RentAsync(CancellationToken ct = default) => Task.FromResult<IDalamudTextureWrap>(this);
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => this;
        public void Dispose() { }
    }
}
