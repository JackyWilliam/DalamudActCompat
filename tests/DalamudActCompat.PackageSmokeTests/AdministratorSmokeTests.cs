using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;

internal static class AdministratorSmokeTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string root)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var old = JsonSerializer.Deserialize<CloudInvitationSummary>("""{"quota":3,"used":3,"remaining":0,"invitations":[]}""", json)!;
        Check(!old.CanGenerate && !old.Administrator.IsAdmin, "An old server response granted unlimited access.");
        Check((old with { IsAdmin = true }).CanGenerate, "An administrator was stopped by zero ordinary quota.");
        if (Environment.GetEnvironmentVariable("DACT_ADMINISTRATOR_FIXTURE") is { Length: > 0 } fixture)
        {
            using var data = JsonDocument.Parse(fixture);
            var user = data.RootElement.GetProperty("user");
            var paths = new PluginPaths(Path.Combine(root, "administrator")); paths.EnsureCreated();
            var disk = new CloudCredentialStore(paths.CloudCredentialFile);
            disk.Save(new(user.GetProperty("username").GetString()!, user.GetProperty("token").GetString()!,
                DateTimeOffset.UtcNow.AddDays(1), new PortableConfigurationBackupService().GenerateRecoveryKey()));
            using var http = new HttpClient { BaseAddress = new(data.RootElement.GetProperty("baseUrl").GetString()!) };
            using var api = new CloudApiClient(http);
            CloudClientService Service() => new(paths, api, disk, new(paths.CloudBanFile), new(paths.CloudDeviceFile),
                new CloudKeyEnvelopeService(), new PortableConfigurationBackupService());
            using (var service = Service())
            {
                await service.InitializeAsync(default);
                Check(service.Snapshot.IsSignedIn && service.Snapshot.Administrator is { IsAdmin: true, AdminNoticePending: true },
                    "Saved login did not deliver the persistent promotion.");
                var grant = service.Snapshot.Administrator!.AdminGrantId!;
                for (var i = 0; i < 5; i++) await service.CreateInvitationAsync("native-client-friend", default);
                Check(service.Snapshot.Invitations is { Used: >= 5, CanGenerate: true, QuotaUsed: 0 }, "Real client failed unlimited generation.");
                await service.AcknowledgeAdministratorAsync("different-account", grant, default);
                Check(service.Snapshot.Administrator!.AdminNoticePending, "Another account's dialog confirmed this promotion.");
                var version = (long)typeof(CloudClientService).GetField("administratorStateVersion",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
                await service.AcknowledgeAdministratorAsync(service.Snapshot.Username!, grant, default);
                Check(service.Snapshot.Administrator is { IsAdmin: true, AdminNoticePending: false }, "Real client confirmation failed.");
                typeof(CloudClientService).GetMethod("ApplyAdministratorStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(service, [disk.Load(), new CloudAdministratorStatus(true, grant, true), version]);
                Check(!service.Snapshot.Administrator!.AdminNoticePending, "A late heartbeat replayed an acknowledged promotion.");
            }
            using (var cold = Service())
            {
                await cold.InitializeAsync(default);
                Check(cold.Snapshot.Administrator is { IsAdmin: true, AdminNoticePending: false }, "Cold auto-login replayed an acknowledged promotion.");
            }
        }
        if (Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI") is { Length: > 0 }) Native();
        Console.WriteLine("Administrator: legacy response, unlimited quota, notification lifecycle and configured native/API checks passed.");
    }

    private static unsafe void Native()
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
            Check(io.Fonts.Build(), "Promotion font atlas failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var icon = LoadIcon(raster);
            var output = Environment.GetEnvironmentVariable("DACT_NATIVE_UI_OUTPUT");
            if (output is not null) Directory.CreateDirectory(output);
            double time = 0; var confirms = new List<(string, string)>();
            var config = new PluginConfiguration();
            var ui = new CloudRoleNotice(new UiText(config), icon, (name, grant) => confirms.Add((name, grant)), () => time);
            var state = CloudClientSnapshot.SignedOut() with { IsSignedIn = true, Username = "test-admin",
                Administrator = new(true, "first-grant", true) };
            void Frame()
            {
                ImGui.NewFrame(); ui.Draw(state); ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0 || !ui.IsOpen || time == 0, "Promotion emitted no native geometry.");
            }
            ui.Update(state); Frame();
            for (var i = 1; i <= 42; i++)
            {
                time = i * .05; Frame();
                if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, $"administrator-{i:00}.png"));
            }
            // Exercise the actual native button and prove the callback carries
            // the account/grant displayed by this dialog, not global login state.
            io.AddMousePosEvent(340, 350); io.AddMouseButtonEvent(0, true); Frame();
            io.AddMouseButtonEvent(0, false); Frame();
            Check(confirms.SequenceEqual([("test-admin", "first-grant")]), "Native confirm did not preserve the displayed identity.");
            state = state with { Administrator = state.Administrator! with { AdminNoticePending = false } };
            ui.Update(state); Check(!ui.IsOpen, "Acknowledged promotion stayed open.");
            state = state with { Administrator = new(true, "second-grant", true) };
            ui.Update(state); Check(ui.IsOpen, "A new promotion did not open.");
            ui.Update(CloudClientSnapshot.SignedOut()); Check(!ui.IsOpen, "Logout left another account's promotion visible.");
            ui.Update(state with { Administrator = new() }); Check(!ui.IsOpen, "Revocation left a promotion visible.");
            config.UiLanguage = "en";
            ui.Update(state); time += 1; Frame();
            if (output is not null) raster.Save(ImGui.GetDrawData(), Path.Combine(output, "administrator-english.png"));
            foreach (var scale in new[] { .75f, 1.5f, 2f })
            {
                io.FontGlobalScale = scale; time += 1; Frame();
                var window = context.Windows[context.Windows.Size - 1];
                Check(window.Pos.X >= 0 && window.Pos.Y >= 0 && window.Pos.X + window.Size.X <= io.DisplaySize.X + 1 &&
                    window.Pos.Y + window.Size.Y <= io.DisplaySize.Y + 1, "Promotion escaped the viewport.");
            }
        }
        finally { ImGui.DestroyContext(context); }
    }

    internal static PreviewIcon LoadIcon(NativeUiRasterizer raster, string filename = "Player26_Icon.png", ulong handle = 999)
    {
        var icon = new PreviewIcon(handle);
        using var bitmap = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", filename));
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y); var offset = (y * bitmap.Width + x) * 4;
            pixels[offset] = c.R; pixels[offset + 1] = c.G; pixels[offset + 2] = c.B; pixels[offset + 3] = c.A;
        }
        raster.AddTexture(icon.Handle, pixels, bitmap.Width, bitmap.Height);
        return icon;
    }

    internal sealed class PreviewIcon(ulong handle = 999) : ISharedImmediateTexture, IDalamudTextureWrap
    {
        public ImTextureID Handle => new(handle);
        public int Width => 32;
        public int Height => 32;
        public Vector2 Size => new(32);
        public IDalamudTextureWrap GetWrapOrEmpty() => this;
        public IDalamudTextureWrap GetWrapOrDefault(IDalamudTextureWrap? fallback = null) => this;
        public bool TryGetWrap([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDalamudTextureWrap? wrap, out Exception? exception)
        { wrap = this; exception = null; return true; }
        public Task<IDalamudTextureWrap> RentAsync(CancellationToken ct = default) => Task.FromResult<IDalamudTextureWrap>(this);
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => this;
        public void Dispose() { }
    }
}
