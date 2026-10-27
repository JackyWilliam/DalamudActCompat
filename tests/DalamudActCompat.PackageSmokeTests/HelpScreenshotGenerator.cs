using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;

// Renders the real control-center pages and meter that the help window illustrates, so the
// pictures always match the shipped UI. Run after a Release build:
//   DACT_TEST_CIMGUI=<cimgui.dll> PackageSmokeTests --help-screenshots <directory>
// then tools/build-help-images.py turns the raw captures into src/DalamudActCompat/Assets/Help.
internal static class HelpScreenshotGenerator
{
    internal static readonly string[] Languages = ["zh-CN", "zh-TW", "en", "ja", "de", "fr"];

    internal static unsafe void Run(string output)
    {
        var library = Environment.GetEnvironmentVariable("DACT_TEST_CIMGUI")
                      ?? throw new InvalidOperationException("Set DACT_TEST_CIMGUI to Dalamud's cimgui.dll.");
        File.Copy(library, Path.Combine(AppContext.BaseDirectory, "cimgui.dll"), true);
        NativeLibrary.Load(library);
        foreach (var language in Languages)
        {
            var directory = Path.Combine(output, language);
            Directory.CreateDirectory(directory);
            Render(directory, language);
        }

        Console.WriteLine($"Help screenshots written to {output}.");
    }

    private static unsafe void Render(string directory, string language)
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null; io.DeltaTime = 1f / 60;
            ushort* ranges = stackalloc ushort[]
            {
                0x20, 0x17f, 0x2000, 0x206f, 0x2190, 0x21ff, 0x2500, 0x27bf,
                0x3000, 0x30ff, 0x4e00, 0x9fff, 0xff00, 0xffef, 0,
            };
            // Japanese UI uses Japanese glyph forms; every other locale renders with YaHei.
            var font = language == "ja" ? "YuGothM.ttc" : "msyh.ttc";
            io.Fonts.AddFontFromFileTTF(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), font), 17, default, ranges);
            if (!io.Fonts.Build()) throw new InvalidOperationException("Help screenshot font failed.");
            var raster = new NativeUiRasterizer(io.Fonts);
            var config = new PluginConfiguration { UiLanguage = language };
            config.Fflogs.Enabled = false;
            // Show the recommended Cactbot layout instead of the legacy combined window.
            config.OverlayWindows = new(StringComparer.OrdinalIgnoreCase)
            {
                [SelfHostedActRuntime.CactbotAlertsOverlayName] = new() { OpenOnStartup = true, HasBeenOpened = true },
                [SelfHostedActRuntime.CactbotTimelineOverlayName] = new() { OpenOnStartup = true, HasBeenOpened = true },
            };
            var text = new UiText(config);
            var logger = new PluginLogger(EntityServiceProxy.Create<IPluginLog>(new()));
            var sample = SampleEncounterFactory.Create(DateTimeOffset.UtcNow);
            using var logo = new SkinSmokeTests.EmptyTexture();
            var fflogs = new FflogsEstimateService(
                () => config.Fflogs,
                Path.Combine(Path.GetTempPath(), $"dact-help-fflogs-{Guid.NewGuid():N}.json"),
                logger);
            DactTheme.SetCurrent(config.Appearance, false, 0);

            var account = new AccountState();
            var window = CreateControlCenter(config, text, logger, fflogs, logo, sample, account);
            // Skip the fade-in: captures must show the fully opened window.
            var transition = typeof(ControlCenterWindow).GetField("visibilityTransition", BindingFlags.NonPublic | BindingFlags.Instance)!;
            transition.SetValue(window, Enum.Parse(transition.FieldType, "Open"));
            var discoveryNotice = typeof(ControlCenterWindow).GetField("discoveryNoticeUntil", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void CaptureControlCenter(string name) => Capture(raster, io, new(900, 640), Path.Combine(directory, name), () =>
            {
                // Hidden-skin discoveries are an easter egg, not something the help should show.
                discoveryNotice.SetValue(window, 0L);
                window.PreDraw();
                ImGui.SetNextWindowPos(new(10, 10)); ImGui.SetNextWindowSize(new(880, 620));
                ImGui.Begin(window.WindowName, window.Flags | ImGuiWindowFlags.NoSavedSettings);
                window.Draw();
                ImGui.End();
                window.PostDraw();
            });

            // Signed out, every page shows the account gate, then the Discord binding gate.
            // These are the first two screens a new user sees.
            CaptureControlCenter("account-login.png");
            account.SignedIn = true;
            CaptureControlCenter("account-discord.png");
            account.DiscordBound = true;
            var pageField = typeof(ControlCenterWindow).GetField("selectedPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var page in Enum.GetValues(pageField.FieldType))
            {
                pageField.SetValue(window, page);
                CaptureControlCenter($"control-{page.ToString()!.ToLowerInvariant()}.png");
            }

            var store = new EncounterStateStore();
            store.UpdateCurrent(sample);
            var meterService = new MeterService(store, config.Meter);
            config.Meter.CompactMode = false;
            var icons = new JobIconTextureSet(null!, Path.Combine(AppContext.BaseDirectory, "missing-icons"));
            var meter = new MeterWindow(meterService, fflogs, config, text, icons, logo, logo, logo, (_, name) => name, () => { });
            Capture(raster, io, new(560, 330), Path.Combine(directory, "meter.png"), () =>
            {
                meter.PreDraw();
                ImGui.SetNextWindowPos(new(10, 10)); ImGui.SetNextWindowSize(new(540, 300));
                ImGui.Begin(meter.WindowName, meter.Flags | ImGuiWindowFlags.NoSavedSettings);
                meter.Draw();
                ImGui.End();
                meter.PostDraw();
            });
        }
        finally
        {
            DactTheme.SetCurrent(new(), false, 0);
            ImGui.DestroyContext(context);
        }
    }

    private static void Capture(NativeUiRasterizer raster, ImGuiIOPtr io, System.Numerics.Vector2 display, string path, Action draw)
    {
        io.DisplaySize = display;
        // Several frames let layout, scrolling and animated sections settle before capture.
        for (var frame = 0; frame < 30; frame++)
        {
            ImGui.NewFrame();
            using (DactTheme.PushFrame())
            {
                draw();
            }

            ImGui.Render();
        }

        raster.Save(ImGui.GetDrawData(), path);
    }

    private static ControlCenterWindow CreateControlCenter(
        PluginConfiguration config,
        UiText text,
        PluginLogger logger,
        FflogsEstimateService fflogs,
        SkinSmokeTests.EmptyTexture logo,
        Encounter sample,
        AccountState account)
    {
        var parser = DispatchProxy.Create<IParserEngine, RunningParser>();
        var cloud = new CloudUiBridge(
            () => new CloudClientSnapshot(
                IsSignedIn: account.SignedIn, IsBusy: false,
                Username: account.SignedIn ? "WarriorOfLight" : null,
                SessionExpiresAt: account.SignedIn ? DateTimeOffset.UtcNow.AddDays(30) : null, Backups: [],
                StatusMessage: string.Empty, StatusIsError: false, RecoveryKeyToSave: null,
                RestorePreview: null, LastRollbackPath: null, HasSavedRecoveryKey: account.SignedIn,
                Recovery: account.SignedIn
                    ? new CloudRecoveryStatus(Available: true, Protected: account.DiscordBound,
                        DiscordBound: account.DiscordBound, BoundAccounts: account.DiscordBound ? 1 : 0, DiscordAvailable: true)
                    : null,
                DiscordLinkCode: account.SignedIn && !account.DiscordBound
                    ? new CloudDiscordLinkCode("DACT-7Q4M-2K9P", DateTimeOffset.UtcNow.AddMinutes(10))
                    : null),
            _ => { }, _ => { }, _ => { }, () => { }, () => { },
            _ => { }, () => { }, _ => { }, _ => { }, () => { });
        var bundled = new (string Id, string Name, string Version, bool Enabled)[]
        {
            ("triggernometry", "Triggernometry CN Maintained Edition", "2.3.0.0", true),
            ("postnamazu", "PostNamazu", "1.3.6.6", true),
            ("act.foxtts", "ACT.FoxTTS", "3.3.1.189", true),
            ("silverdasher", "银山雀儿 / SilverDasher", "0.6.0.4", false),
            ("matcha", "抹茶 / Cafe.Matcha", "26.9.26.1753", true),
        }.Select(plugin => new InstalledActPlugin(
            new ActPluginManifest { Id = plugin.Id, Name = plugin.Name, Version = plugin.Version },
            Path.Combine(Path.GetTempPath(), "dact-help", plugin.Id),
            plugin.Enabled)).ToArray();
        var overrides = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["configuration"] = config,
            ["parserEngine"] = parser,
            ["logger"] = logger,
            ["text"] = text,
            ["fflogsEstimateService"] = fflogs,
            ["combatQualitySnapshot"] = null,
            ["logoTexture"] = logo,
            ["cloud"] = cloud,
        };
        const string raidboss = "file:///C:/DACT/cactbot/ui/raidboss/raidboss.html";
        var returns = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["getOverlayTemplates"] = (IReadOnlyList<ActOverlayTemplate>)
            [
                new(SelfHostedActRuntime.CactbotAlertsOverlayName, raidboss + "?alerts=1&timeline=0", 900, 320, true),
                new(SelfHostedActRuntime.CactbotTimelineOverlayName, raidboss + "?alerts=0&timeline=1", 600, 300, true),
            ],
            ["getCurrentEncounter"] = sample,
            ["isCactbotInstalled"] = true,
            ["discoverPlugins"] = (IReadOnlyList<InstalledActPlugin>)bundled,
            ["getCombatLogDirectory"] = @"C:\Users\<you>\AppData\Roaming\XIVLauncherCN\pluginConfigs\DalamudActCompat\logs",
        };
        var constructor = typeof(ControlCenterWindow).GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => overrides.TryGetValue(parameter.Name!, out var value)
                ? value
                : typeof(Delegate).IsAssignableFrom(parameter.ParameterType)
                    ? Stub(parameter.ParameterType, returns.TryGetValue(parameter.Name!, out var result) ? result : null, returns.ContainsKey(parameter.Name!))
                    : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
            .ToArray();
        return (ControlCenterWindow)constructor.Invoke(arguments);
    }

    private sealed class AccountState
    {
        public bool SignedIn { get; set; }

        public bool DiscordBound { get; set; }
    }

    // Reports a running parser; event subscriptions and lifecycle calls are ignored.
    public class RunningParser : DispatchProxy
    {
        private static readonly ParserStatus Running = new(
            ParserState.Running,
            "FFXIV_ACT_Plugin and OverlayPlugin are running in DalamudActCompat.",
            DateTimeOffset.UtcNow);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "get_Status" ? Running : Neutral(method?.ReturnType ?? typeof(void));
    }

    // Builds a callback that ignores its arguments and returns a neutral value for its type.
    private static Delegate Stub(Type delegateType, object? value, bool hasValue)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Constant(hasValue ? value : Neutral(invoke.ReturnType), invoke.ReturnType);
        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    private static object? Neutral(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return string.Empty;
        if (type == typeof(ThirdPartyPluginInstallStatus)) return new ThirdPartyPluginInstallStatus(ThirdPartyPluginInstallState.Idle);
        if (type == typeof(ResourcePackOperationStatus)) return ResourcePackOperationStatus.Ready();
        if (type == typeof(CactbotOperationStatus)) return new CactbotOperationStatus(CactbotOperationState.Ready);
        if (type == typeof(GameRegionSelection))
            return new GameRegionSelection(GameRegionMode.Auto, HostGameRegion.Chinese, HostGameRegion.Chinese, HostClientLanguage.Chinese, "ChineseSimplified", 4);
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GenericTypeArguments[0])
                .Invoke(null, [Neutral(type.GenericTypeArguments[0])]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>)))
            return Array.CreateInstance(type.GenericTypeArguments[0], 0);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
