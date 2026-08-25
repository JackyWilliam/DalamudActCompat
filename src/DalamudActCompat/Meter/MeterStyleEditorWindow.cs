using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;
using System.Reflection;

namespace DalamudActCompat.Meter;

public sealed class MeterStyleEditorWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly MeterWindow meterWindow;
    private readonly HorizontalMeterWindow horizontalMeterWindow;
    private readonly RoleSplitMeterWindow roleSplitDamageWindow;
    private readonly RoleSplitMeterWindow roleSplitHealerWindow;
    private readonly Encounter previewEncounter;
    private readonly IReadOnlyList<CombatantRow> previewRows;
    private readonly UiText text;
    private readonly Action saveConfiguration;
    private MeterWindowKind selectedKind;
    private string? selectedSlotId;

    public MeterStyleEditorWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture logoTexture,
        MeterWindow meterWindow,
        HorizontalMeterWindow horizontalMeterWindow,
        RoleSplitMeterWindow roleSplitDamageWindow,
        RoleSplitMeterWindow roleSplitHealerWindow,
        UiText text,
        Action saveConfiguration)
        : base("战斗统计布局编辑器###DalamudActCompatMeterStyleEditor")
    {
        this.configuration = configuration;
        this.logoTexture = logoTexture;
        this.meterWindow = meterWindow;
        this.horizontalMeterWindow = horizontalMeterWindow;
        this.roleSplitDamageWindow = roleSplitDamageWindow;
        this.roleSplitHealerWindow = roleSplitHealerWindow;
        this.text = text;
        this.saveConfiguration = saveConfiguration;
        previewEncounter = CreatePreviewEncounter();
        previewRows = CreatePreviewRows(previewEncounter);
        Size = new Vector2(1040, 690);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 590),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public void Open()
    {
        SetEditingProfile(null);
        selectedKind = configuration.Meter.ActiveWindowKind;
        SetEditingProfile(selectedKind);
        EnsureSelectedSlot(CurrentProfile);
        IsOpen = true;
    }

    public override void OnClose()
    {
        SetEditingProfile(null);
        saveConfiguration();
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.055f, 0.075f, 0.10f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.14f, 0.34f, 0.46f, 0.30f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(9);
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "战斗统计布局编辑器###DalamudActCompatMeterStyleEditor",
            "Combat Meter Layout Editor###DalamudActCompatMeterStyleEditor");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "--";
        if (BrandedWindowChrome.Draw(
                logoTexture,
                text.Get("布局编辑器", "Layout editor"),
                KindLabel(selectedKind),
                IceBlue,
                $"v{version}",
                "meter-style-editor"))
        {
            SetEditingProfile(null);
            IsOpen = false;
            return;
        }

        var selectedIndex = BrandedWindowChrome.DrawNavigationRail(
            "meter-editor-kind",
            [
                text.Get("经典榜", "Classic"),
                text.Get("透明横版", "Transparent horizontal"),
                text.Get("职能分栏", "Role split"),
            ],
            (int)selectedKind);
        if (selectedIndex != (int)selectedKind)
        {
            SetEditingProfile(null);
            selectedKind = (MeterWindowKind)selectedIndex;
            SetEditingProfile(selectedKind);
            selectedSlotId = null;
            EnsureSelectedSlot(CurrentProfile);
        }
        ImGui.Dummy(new Vector2(1, 6));

        var changed = DrawWindowControls(CurrentProfile);
        ImGui.Dummy(new Vector2(1, 5));
        var available = ImGui.GetContentRegionAvail();
        const float leftWidth = 255;
        const float rightWidth = 270;
        if (ImGui.BeginChild("meter-editor-slots", new Vector2(leftWidth, available.Y), true))
        {
            changed |= DrawSlotList(CurrentProfile);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild(
                "meter-editor-preview",
                new Vector2(Math.Max(250, available.X - leftWidth - rightWidth - 16), available.Y),
                true))
        {
            changed |= DrawPreview(CurrentProfile);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("meter-editor-properties", new Vector2(rightWidth, available.Y), true))
        {
            changed |= DrawSlotProperties(CurrentProfile);
        }
        ImGui.EndChild();

        if (changed)
        {
            CurrentProfile.Normalize(DefaultSlots(selectedKind));
            SynchronizeClassicSettings();
            saveConfiguration();
        }
    }

    private MeterWindowProfile CurrentProfile => selectedKind switch
    {
        MeterWindowKind.Horizontal => configuration.Meter.HorizontalWindow,
        MeterWindowKind.RoleSplit => configuration.Meter.RoleSplitWindow,
        _ => configuration.Meter.ClassicWindow,
    };

    private bool DrawWindowControls(MeterWindowProfile profile)
    {
        var changed = false;
        var active = profile.IsEnabled;
        ImGui.BeginDisabled(active);
        if (ImGui.Button(
                active
                    ? text.Get("当前模板已启用", "This template is enabled")
                    : text.Get("启用此模板", "Enable this template"),
                new Vector2(180, 0)))
        {
            configuration.Meter.ActivateWindow(selectedKind);
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(text.Get(
            "启用后会自动停用另外两个模板。",
            "Enabling this template disables the other two."));

        var locked = profile.IsLocked;
        if (ImGui.Checkbox(text.Get("锁定", "Lock"), ref locked))
        {
            profile.IsLocked = locked;
            changed = true;
        }
        ImGui.SameLine();
        var clickThrough = profile.ClickThroughWhenLocked;
        ImGui.BeginDisabled(!profile.IsLocked);
        if (ImGui.Checkbox(text.Get("锁定时鼠标穿透", "Click-through when locked"), ref clickThrough))
        {
            profile.ClickThroughWhenLocked = clickThrough;
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        var autoHide = profile.AutoHideOutOfCombat;
        if (ImGui.Checkbox(text.Get("脱战隐藏", "Hide out of combat"), ref autoHide))
        {
            profile.AutoHideOutOfCombat = autoHide;
            changed = true;
        }
        if (selectedKind == MeterWindowKind.Classic)
        {
            var compact = configuration.Meter.CompactMode;
            ImGui.SameLine();
            if (ImGui.Checkbox(text.Get("只显示自己", "Show self only"), ref compact))
            {
                configuration.Meter.CompactMode = compact;
                changed = true;
            }
        }
        return changed;
    }

    private bool DrawSlotList(MeterWindowProfile profile)
    {
        var changed = false;
        ImGui.TextColored(Gold, text.Get("槽位", "Complication slots"));
        if (selectedKind == MeterWindowKind.Classic && configuration.Meter.ClassicAllianceView)
        {
            ImGui.TextWrapped(text.Get(
                "24 人本使用固定紧凑条，只显示职业 / 名字和当前 DPS/HPS，不提供槽位调整。切回 8 人本可编辑经典表格列。",
                "The 24-player compact row is fixed to job/name and the current DPS/HPS value. Switch to 8-player mode to edit classic table columns."));
            return false;
        }
        ImGui.TextDisabled(text.Get(
            "点击槽位后选择内容；系统自动排布，不会重叠。",
            "Select a slot and choose its content. Layout never overlaps."));
        ImGui.Separator();
        for (var index = 0; index < profile.Slots.Count; index++)
        {
            var slot = profile.Slots[index];
            var visible = slot.Visible;
            var canEnable = CanUseMetric(slot.Metric);
            ImGui.BeginDisabled(!canEnable);
            if (ImGui.Checkbox($"##slot-visible-{slot.Id}", ref visible))
            {
                slot.Visible = visible;
                changed = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Selectable(
                    $"{index + 1:00}  {MeterSlotPresentation.Label(slot.Metric, text)}##slot-{slot.Id}",
                    string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase)))
            {
                selectedSlotId = slot.Id;
            }
            if (!canEnable && slot.Metric == MeterSlotMetric.Fflogs && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get(
                    "请先在设置页启用 FFLogs 在线估算。",
                    "Enable FFLogs online estimates in Settings first."));
            }
        }

        ImGui.Dummy(new Vector2(1, 4));
        if (ImGui.Button(text.Get("＋ 添加槽位", "+ Add slot"), new Vector2(-1, 0)))
        {
            var slot = new MeterSlotDefinition(
                FirstUnusedMetric(profile),
                0,
                0,
                4,
                2,
                MeterSlotAlignment.Left);
            profile.Slots.Add(slot);
            selectedSlotId = slot.Id;
            changed = true;
        }
        if (ImGui.Button(text.Get("恢复此模板默认槽位", "Restore template slots"), new Vector2(-1, 0)))
        {
            profile.Slots = DefaultSlots(selectedKind).Select(static slot => slot.Clone()).ToList();
            selectedSlotId = profile.Slots.FirstOrDefault()?.Id;
            changed = true;
        }
        return changed;
    }

    private bool DrawPreview(MeterWindowProfile profile)
    {
        ImGui.TextColored(Gold, text.Get("真实页面预览", "Live page preview"));
        ImGui.TextDisabled(KindDescription(selectedKind));
        ImGui.Separator();
        ImGui.TextDisabled(text.Get(
            "此处直接调用悬浮窗的运行渲染；左侧列表用于选择槽位。",
            "This area calls the runtime overlay renderer directly; use the list on the left to select a slot."));
        ImGui.Dummy(new Vector2(1, 4));
        var availableHeight = Math.Max(220, ImGui.GetContentRegionAvail().Y);
        switch (selectedKind)
        {
            case MeterWindowKind.Horizontal:
                DrawRuntimePreviewFrame(
                    "horizontal-runtime-preview",
                    availableHeight,
                    Vector4.Zero,
                    Vector4.Zero,
                    () => horizontalMeterWindow.DrawEditorPreview(previewEncounter, previewRows));
                break;
            case MeterWindowKind.RoleSplit:
                var roleHeight = Math.Max(210, (availableHeight - 8) * 0.5f);
                DrawRuntimePreviewFrame(
                    "role-damage-runtime-preview",
                    roleHeight,
                    MeterWindow.ApplyBackgroundOpacity(Navy, profile.BackgroundOpacity),
                    MeterWindow.ApplyBackgroundOpacity(Gold, profile.BackgroundOpacity),
                    () => roleSplitDamageWindow.DrawEditorPreview(previewEncounter, previewRows));
                ImGui.Dummy(new Vector2(1, 8));
                DrawRuntimePreviewFrame(
                    "role-healer-runtime-preview",
                    roleHeight,
                    MeterWindow.ApplyBackgroundOpacity(Navy, profile.BackgroundOpacity),
                    MeterWindow.ApplyBackgroundOpacity(Gold, profile.BackgroundOpacity),
                    () => roleSplitHealerWindow.DrawEditorPreview(previewEncounter, previewRows));
                break;
            default:
                DrawRuntimePreviewFrame(
                    "classic-runtime-preview",
                    availableHeight,
                    MeterWindow.ApplyBackgroundOpacity(Navy, profile.BackgroundOpacity),
                    MeterWindow.ApplyBackgroundOpacity(Gold, profile.BackgroundOpacity),
                    () => meterWindow.DrawEditorPreview(previewEncounter, previewRows));
                break;
        }
        return false;
    }

    private static void DrawRuntimePreviewFrame(
        string id,
        float height,
        Vector4 background,
        Vector4 border,
        Action draw)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        if (ImGui.BeginChild(id, new Vector2(-1, height), true))
        {
            draw();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private static Encounter CreatePreviewEncounter()
    {
        string[] jobs =
        [
            "PLD", "WAR", "WHM", "SCH", "SAM", "NIN", "BRD", "BLM",
            "DRK", "GNB", "AST", "SGE", "DRG", "RPR", "MCH", "SMN",
            "PLD", "WAR", "WHM", "SCH", "MNK", "VPR", "DNC", "PCT",
        ];
        var combatants = jobs.Select((job, index) =>
        {
            var dps = 15_800 - (index * 410);
            var isHealer = JobRoleClassifier.IsHealer(job);
            return new Combatant(
                $"preview-{index + 1}",
                index == 0 ? "自己" : $"队友 {index:00}",
                job,
                index == 0,
                dps * 180L,
                isHealer ? (9_200 - (index * 70)) * 180L : 120_000 + (index * 4_000L),
                index % 11 == 0 ? 1 : 0,
                Dps: dps,
                EncDps: dps,
                ExtDps: dps,
                DamageHits: 180 + index,
                CriticalHits: 54 + index,
                CriticalDirectHits: 18 + (index / 2),
                Rdps: dps + 240,
                DirectHits: 43 + index,
                HighestDamageAction: index % 2 == 0 ? "爆发击" : "强力技能",
                HighestDamage: 128_000 - (index * 1_700L),
                PartyGroup: (index / 8) + 1);
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        return new Encounter(
            Guid.Parse("7b2b7ff6-1a44-4d5e-8ec7-14053c7d2224"),
            now.AddMinutes(-3),
            null,
            "预览副本",
            "预览战斗",
            combatants,
            [],
            [],
            [],
            [],
            [])
        {
            CombatDuration = TimeSpan.FromMinutes(3),
            PartyCapacity = 24,
        };
    }

    private static IReadOnlyList<CombatantRow> CreatePreviewRows(Encounter encounter)
    {
        var totalDamage = Math.Max(1, encounter.TotalDamage);
        return encounter.Combatants.Select((combatant, index) =>
            new CombatantRow(
                combatant.Id,
                combatant.Name,
                combatant.Job,
                combatant.IsLocalPlayer,
                combatant.Dps,
                combatant.TotalHealing / 180d,
                combatant.TotalDamage,
                combatant.TotalHealing,
                combatant.TotalDamage * 100d / totalDamage,
                combatant.CriticalHits * 100d / combatant.DamageHits,
                combatant.DirectHits * 100d / combatant.DamageHits,
                combatant.CriticalDirectHits * 100d / combatant.DamageHits,
                combatant.Deaths,
                Rank: index + 1,
                HighestDamageAction: combatant.HighestDamageAction,
                HighestDamage: combatant.HighestDamage,
                PartyGroup: combatant.PartyGroup,
                PersonalDps: combatant.Dps,
                Rdps: combatant.Rdps,
                EncDps: combatant.EncDps,
                ExtDps: combatant.ExtDps)).ToArray();
    }

    private bool DrawSlotProperties(MeterWindowProfile profile)
    {
        var changed = false;
        ImGui.TextColored(Gold, text.Get("窗口与槽位", "Window and slot"));
        var showHeader = profile.ShowHeader;
        if (selectedKind != MeterWindowKind.Horizontal &&
            ImGui.Checkbox(text.Get("显示标题区", "Show header"), ref showHeader))
        {
            profile.ShowHeader = showHeader;
            changed = true;
        }
        var fontScale = profile.FontScale;
        if (DrawLabeledSlider(
                "profile-font-scale",
                text.Get("字号", "Text scale"),
                text.Get("只调整当前模板的文字与行高。", "Adjusts text and row height for this template only."),
                ref fontScale,
                0.65f,
                2,
                "%.2f"))
        {
            profile.FontScale = fontScale;
            changed = true;
        }
        if (selectedKind == MeterWindowKind.Horizontal)
        {
            var itemWidth = profile.ItemWidth;
            if (DrawLabeledSlider(
                    "horizontal-module-width",
                    text.Get("模块宽度", "Module width"),
                    text.Get("调整横向滑动序列中每名玩家占用的宽度。", "Sets each player's width in the horizontal carousel."),
                    ref itemWidth,
                    140,
                    420,
                    "%.0f px"))
            {
                profile.ItemWidth = itemWidth;
                changed = true;
            }
        }
        if (selectedKind != MeterWindowKind.Horizontal)
        {
            var backgroundOpacity = profile.BackgroundOpacity;
            if (DrawLabeledSlider(
                    "profile-background-opacity",
                    text.Get("背景透明度", "Background opacity"),
                    text.Get("0 为完全透明，1 为完全不透明；透明横版始终无背景。", "0 is fully transparent and 1 is opaque; the horizontal template never draws a background."),
                    ref backgroundOpacity,
                    0,
                    1,
                    "%.2f"))
            {
                profile.BackgroundOpacity = backgroundOpacity;
                changed = true;
            }
        }
        var localPlayerColor = configuration.Meter.LocalPlayerColor;
        if (ImGui.ColorEdit4(text.Get("自己的强调色", "Local player accent"), ref localPlayerColor))
        {
            configuration.Meter.LocalPlayerColor = localPlayerColor;
            changed = true;
        }
        ImGui.Separator();

        if (selectedKind == MeterWindowKind.Classic && configuration.Meter.ClassicAllianceView)
        {
            ImGui.TextColored(IceBlue, text.Get("24 人本固定布局", "Fixed 24-player layout"));
            ImGui.TextWrapped(text.Get(
                "固定显示职业 / 名字和当前 DPS/HPS；没有可调整槽位。上方 8/24 下拉菜单切回 8 人本后可继续编辑经典表格。",
                "Shows only job/name and the current DPS/HPS value. Switch the 8/24 menu above back to 8-player mode to edit classic table slots."));
            return changed;
        }

        var slot = profile.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            ImGui.TextDisabled(text.Get("请先选择一个槽位。", "Select a slot first."));
            return changed;
        }

        ImGui.TextColored(IceBlue, text.Get("槽位内容", "Slot content"));
        var preview = MeterSlotPresentation.Label(slot.Metric, text);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##slot-metric", preview))
        {
            foreach (var metric in MeterSlotDefaults.EditableMetrics.Where(CanUseMetric))
            {
                if (ImGui.Selectable(
                        $"{MeterSlotPresentation.Label(metric, text)}##metric-{metric}",
                        metric == slot.Metric))
                {
                    slot.Metric = metric;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (slot.Metric == MeterSlotMetric.PlayerIdentity)
        {
            var jobStyle = configuration.Meter.JobDisplayStyle;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo(
                    text.Get("职业显示方式", "Job display"),
                    JobDisplayFormatter.Label(jobStyle, text)))
            {
                foreach (var style in Enum.GetValues<JobDisplayStyle>())
                {
                    if (ImGui.Selectable(
                            JobDisplayFormatter.Label(style, text),
                            style == jobStyle))
                    {
                        configuration.Meter.JobDisplayStyle = style;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }
        }
        var visible = slot.Visible;
        var canUseSelectedSlot = CanUseMetric(slot.Metric);
        ImGui.BeginDisabled(!canUseSelectedSlot);
        if (ImGui.Checkbox(text.Get("使用这个槽位", "Use this slot"), ref visible))
        {
            slot.Visible = visible;
            changed = true;
        }
        ImGui.EndDisabled();
        if (!canUseSelectedSlot && slot.Metric == MeterSlotMetric.Fflogs)
        {
            ImGui.TextWrapped(text.Get(
                "先在设置页启用 FFLogs 在线估算，才能打开这个列。原有配置会保留。",
                "Enable FFLogs online estimates in Settings before showing this column. Its saved configuration is preserved."));
        }
        ImGui.Dummy(new Vector2(1, 8));
        var index = profile.Slots.IndexOf(slot);
        ImGui.BeginDisabled(index <= 0);
        if (ImGui.Button(text.Get("↑ 前移", "↑ Move up"), new Vector2(-1, 0)))
        {
            (profile.Slots[index - 1], profile.Slots[index]) =
                (profile.Slots[index], profile.Slots[index - 1]);
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(index < 0 || index >= profile.Slots.Count - 1);
        if (ImGui.Button(text.Get("↓ 后移", "↓ Move down"), new Vector2(-1, 0)))
        {
            (profile.Slots[index + 1], profile.Slots[index]) =
                (profile.Slots[index], profile.Slots[index + 1]);
            changed = true;
        }
        ImGui.EndDisabled();
        if (ImGui.Button(text.Get("删除槽位", "Delete slot"), new Vector2(-1, 0)))
        {
            profile.Slots.Remove(slot);
            selectedSlotId = profile.Slots.Count == 0
                ? null
                : profile.Slots[Math.Min(index, profile.Slots.Count - 1)].Id;
            changed = true;
        }

        ImGui.Dummy(new Vector2(1, 8));
        ImGui.TextDisabled(text.Get(
            "最高伤害会自动截短；把鼠标移到统计项上可查看完整技能名和数值。",
            "Max-hit skills are truncated; hover the meter for full details."));
        return changed;
    }

    private static bool DrawLabeledSlider(
        string id,
        string label,
        string hint,
        ref float value,
        float minimum,
        float maximum,
        string format)
    {
        ImGui.TextUnformatted(label);
        ImGui.TextDisabled(hint);
        ImGui.SetNextItemWidth(-1);
        return ImGui.SliderFloat($"##{id}", ref value, minimum, maximum, format);
    }

    private void SynchronizeClassicSettings()
    {
        var meter = configuration.Meter;
        var profile = meter.ClassicWindow;
        meter.IsLocked = profile.IsLocked;
        meter.ClickThroughWhenLocked = profile.ClickThroughWhenLocked;
        meter.AutoHideOutOfCombat = profile.AutoHideOutOfCombat;
        meter.ShowHeader = profile.ShowHeader;
        meter.FontScale = profile.FontScale;
        meter.BackgroundOpacity = profile.BackgroundOpacity;
        if (meter.ActiveWindowKind == MeterWindowKind.Classic)
        {
            meter.SortMode = profile.SortMode;
        }
        bool Has(MeterSlotMetric metric) => profile.Slots.Any(slot => slot.Visible && slot.Metric == metric);
        meter.ShowRank = Has(MeterSlotMetric.Rank);
        var showPlayerIdentity = Has(MeterSlotMetric.PlayerIdentity);
        meter.ShowJob = showPlayerIdentity;
        meter.ShowPlayerName = showPlayerIdentity;
        meter.ShowDps = Has(MeterSlotMetric.Dps);
        meter.ShowRdps = Has(MeterSlotMetric.Rdps);
        meter.ShowEncDps = Has(MeterSlotMetric.EncDps);
        meter.ShowExtDps = Has(MeterSlotMetric.ExtDps);
        meter.ShowHps = Has(MeterSlotMetric.Hps);
        meter.ShowDamagePercent = Has(MeterSlotMetric.DamagePercent);
        meter.ShowTotalDamage = Has(MeterSlotMetric.TotalDamage);
        meter.ShowTotalHealing = Has(MeterSlotMetric.TotalHealing);
        meter.ShowHighestDamage = Has(MeterSlotMetric.HighestDamageAction) || Has(MeterSlotMetric.HighestDamage);
        meter.ShowDeaths = Has(MeterSlotMetric.Deaths);
        meter.ShowCriticalHitRate = Has(MeterSlotMetric.CriticalHitPercent);
        meter.ShowDirectHitRate = Has(MeterSlotMetric.DirectHitPercent);
        meter.ShowCriticalDirectHitRate = Has(MeterSlotMetric.CriticalDirectHitPercent);
        meter.ShowFflogs = Has(MeterSlotMetric.Fflogs);
    }

    private void EnsureSelectedSlot(MeterWindowProfile profile)
    {
        if (!profile.Slots.Any(slot => string.Equals(
                slot.Id,
                selectedSlotId,
                StringComparison.OrdinalIgnoreCase)))
        {
            selectedSlotId = profile.Slots.FirstOrDefault()?.Id;
        }
    }

    private MeterSlotMetric FirstUnusedMetric(MeterWindowProfile profile)
        => MeterSlotDefaults.EditableMetrics.Where(CanUseMetric)
            .FirstOrDefault(metric => profile.Slots.All(slot => slot.Metric != metric));

    private bool CanUseMetric(MeterSlotMetric metric)
        => metric != MeterSlotMetric.Fflogs ||
           (selectedKind == MeterWindowKind.Classic && configuration.Fflogs.Enabled);

    private void SetEditingProfile(MeterWindowKind? kind)
    {
        configuration.Meter.ClassicWindow.IsEditing = kind == MeterWindowKind.Classic;
        configuration.Meter.HorizontalWindow.IsEditing = kind == MeterWindowKind.Horizontal;
        configuration.Meter.RoleSplitWindow.IsEditing = kind == MeterWindowKind.RoleSplit;
    }

    private static IReadOnlyList<MeterSlotDefinition> DefaultSlots(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => MeterSlotDefaults.CreateHorizontal(),
            MeterWindowKind.RoleSplit => MeterSlotDefaults.CreateRoleSplit(),
            _ => MeterSlotDefaults.CreateClassic(),
        };

    private string KindLabel(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => text.Get("透明横版", "Transparent horizontal"),
            MeterWindowKind.RoleSplit => text.Get("职能分栏", "Role split"),
            _ => text.Get("经典榜", "Classic"),
        };

    private string KindDescription(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => text.Get(
                "横向滑动；DPS/HPS 从高到低排列；运行时完全透明。",
                "Horizontal carousel; DPS/HPS descending; fully transparent at runtime."),
            MeterWindowKind.RoleSplit => text.Get(
                "D/T 与治疗是两个独立窗口，黄色框内均可点击调整。",
                "D/T and healer are separate windows; click any yellow-framed item to adjust it."),
            _ => text.Get(
                "玩家方块从左到右排列；总伤害与总治疗固定在队伍汇总区。",
                "Player tiles flow left to right; team damage and healing stay in the summary area."),
        };

}
