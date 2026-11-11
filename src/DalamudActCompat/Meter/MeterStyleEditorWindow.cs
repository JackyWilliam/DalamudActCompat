using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using DalamudActCompat.UI.Kit;
using Newtonsoft.Json;
using System.Numerics;
using System.Reflection;

namespace DalamudActCompat.Meter;

public sealed partial class MeterStyleEditorWindow : Window
{
    private static Vector4 Navy => DactTheme.Tone(new Vector4(0.035f, 0.048f, 0.068f, 1), DactTheme.Palette.Surface);
    private static Vector4 NavyRaised => DactTheme.Tone(new Vector4(0.070f, 0.095f, 0.125f, 1), DactTheme.Palette.Raised);
    private static Vector4 NavyHover => DactTheme.Tone(new Vector4(0.105f, 0.145f, 0.185f, 1), DactTheme.Palette.Hover);
    private static Vector4 Gold => DactTheme.Tone(new Vector4(0.78f, 0.66f, 0.36f, 1), DactTheme.Palette.Gold);
    private static Vector4 IceBlue => DactTheme.Tone(new Vector4(0.42f, 0.78f, 0.96f, 1), DactTheme.Palette.Accent);
    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly MeterWindow meterWindow;
    private readonly HorizontalMeterWindow horizontalMeterWindow;
    private readonly RoleSplitMeterWindow roleSplitDamageWindow;
    private readonly RoleSplitMeterWindow roleSplitHealerWindow;
    private Encounter previewEncounter;
    private IReadOnlyList<CombatantRow> previewRows;
    private string previewLanguage;
    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly Action saveConfiguration;
    private MeterWindowKind selectedKind;
    private RoleSplitGroup selectedRoleSplitGroup;
    private string? selectedSlotId;
    private MeterPreviewInteraction? previewInteraction;
    private MeterPreviewInteraction? roleSplitDamagePreviewInteraction;
    private MeterPreviewInteraction? roleSplitHealerPreviewInteraction;
    private string? editingSnapshot;
    private bool closeActionHandled;
    private bool exitConfirmationRequested;

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
        previewLanguage = text.Language;
        previewEncounter = CreatePreviewEncounter(text);
        previewRows = CreatePreviewRows(previewEncounter);
        Size = new Vector2(1040, 690);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 590),
            MaximumSize = new Vector2(float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
    }

    public void Open()
    {
        // The editor is a transaction: live previews use the working configuration,
        // while Cancel must restore the exact state that existed when the window opened.
        editingSnapshot = JsonConvert.SerializeObject(configuration.Meter);
        closeActionHandled = false;
        exitConfirmationRequested = false;
        SetEditingProfile(null);
        selectedKind = configuration.Meter.ActiveWindowKind;
        selectedRoleSplitGroup = RoleSplitGroup.DamageTank;
        SetEditingProfile(selectedKind);
        EnsureSelectedSlot(CurrentSlots);
        ResetPreviewInteraction();
        IsOpen = true;
    }

    public override void OnClose()
    {
        if (!closeActionHandled)
        {
            RestoreEditingSnapshot();
            saveConfiguration();
        }
        SetEditingProfile(null);
    }

    private MeterWindowProfile CurrentProfile => selectedKind switch
    {
        MeterWindowKind.Horizontal => configuration.Meter.HorizontalWindow,
        MeterWindowKind.RoleSplit when selectedRoleSplitGroup == RoleSplitGroup.Healer =>
            configuration.Meter.RoleSplitHealerWindow,
        MeterWindowKind.RoleSplit => configuration.Meter.RoleSplitDamageWindow,
        _ => configuration.Meter.ClassicWindow,
    };

    private List<MeterSlotDefinition> CurrentSlots => CurrentProfile.Slots;

    private bool DrawPreview(MeterWindowProfile profile, float availableHeight)
    {
        switch (selectedKind)
        {
            case MeterWindowKind.Horizontal:
            {
                var interaction = previewInteraction ??= CreatePreviewInteraction(CurrentSlots);
                DrawRuntimePreviewFrame(
                    "horizontal-runtime-preview",
                    availableHeight,
                    MeterBackground.Fill(profile),
                    Vector4.Zero,
                    () => horizontalMeterWindow.DrawEditorPreview(
                        previewEncounter,
                        previewRows,
                        interaction));
                interaction.EndFrame();
                return interaction.ConsumeChanged();
            }
            case MeterWindowKind.RoleSplit:
            {
                var damageProfile = configuration.Meter.RoleSplitDamageWindow;
                var healerProfile = configuration.Meter.RoleSplitHealerWindow;
                var damageInteraction = roleSplitDamagePreviewInteraction ??=
                    CreateRoleSplitPreviewInteraction(RoleSplitGroup.DamageTank);
                var healerInteraction = roleSplitHealerPreviewInteraction ??=
                    CreateRoleSplitPreviewInteraction(RoleSplitGroup.Healer);
                var roleHeight = Math.Max(210, (availableHeight - 8) * 0.5f);
                if (DrawRuntimePreviewFrame(
                    "role-damage-runtime-preview",
                    roleHeight,
                    MeterBackground.Fill(damageProfile),
                    MeterWindow.ApplyBackgroundOpacity(
                        selectedRoleSplitGroup == RoleSplitGroup.DamageTank
                            ? Gold
                            : new Vector4(Gold.X, Gold.Y, Gold.Z, 0.28f),
                        damageProfile.BackgroundOpacity),
                    () => roleSplitDamageWindow.DrawEditorPreview(
                        previewEncounter,
                        previewRows,
                        damageInteraction)))
                {
                    SelectRoleSplitGroup(RoleSplitGroup.DamageTank);
                }
                ImGui.Dummy(new Vector2(1, 8));
                if (DrawRuntimePreviewFrame(
                    "role-healer-runtime-preview",
                    roleHeight,
                    MeterBackground.Fill(healerProfile),
                    MeterWindow.ApplyBackgroundOpacity(
                        selectedRoleSplitGroup == RoleSplitGroup.Healer
                            ? Gold
                            : new Vector4(Gold.X, Gold.Y, Gold.Z, 0.28f),
                        healerProfile.BackgroundOpacity),
                    () => roleSplitHealerWindow.DrawEditorPreview(
                        previewEncounter,
                        previewRows,
                        healerInteraction)))
                {
                    SelectRoleSplitGroup(RoleSplitGroup.Healer);
                }
                damageInteraction.EndFrame();
                healerInteraction.EndFrame();
                return damageInteraction.ConsumeChanged() |
                       healerInteraction.ConsumeChanged();
            }
            default:
            {
                var interaction = previewInteraction ??= CreatePreviewInteraction(CurrentSlots);
                DrawRuntimePreviewFrame(
                    "classic-runtime-preview",
                    availableHeight,
                    MeterBackground.Fill(profile),
                    MeterWindow.ApplyBackgroundOpacity(Gold, profile.BackgroundOpacity),
                    () => meterWindow.DrawEditorPreview(
                        previewEncounter,
                        previewRows,
                        interaction));
                interaction.EndFrame();
                return interaction.ConsumeChanged();
            }
        }
    }

    private static bool DrawRuntimePreviewFrame(
        string id,
        float height,
        Vector4 background,
        Vector4 border,
        Action draw)
    {
        // A meter has its own background, independent of the surrounding skin.
        // Preview it over a neutral dark surface, like the runtime overlay.
        var start = ImGui.GetCursorScreenPos();
        var s = DactUi.S;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(ImGui.GetContentRegionAvail().X, height), ImGui.GetColorU32(new Vector4(.06f, .075f, .09f, 1)), 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        MeterBackground.PushText(new MeterWindowProfile { BackgroundColor = new Vector3(background.X, background.Y, background.Z), BackgroundOpacity = background.W });
        if (ImGui.BeginChild(id, new Vector2(-1, height), true))
        {
            draw();
        }
        var clicked = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) &&
                      ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        return clicked;
    }

    private void RequestExitConfirmation()
    {
        if (!HasUnsavedConfigurationChanges())
        {
            // Template selection and editor-preview flags are transient. A clean editor
            // can close immediately because there is no user configuration to discard.
            CloseWithoutChanges();
            return;
        }

        exitConfirmationRequested = true;
    }

    private bool HasUnsavedConfigurationChanges()
    {
        if (string.IsNullOrWhiteSpace(editingSnapshot))
        {
            return false;
        }

        return !string.Equals(
            editingSnapshot,
            SerializeForChangeDetection(configuration.Meter),
            StringComparison.Ordinal);
    }

    private static string SerializeForChangeDetection(MeterSettings settings)
        // IsEditing is excluded from JSON, so comparing serialized values ignores tab
        // browsing without rehydrating default-filled collections into the snapshot.
        => JsonConvert.SerializeObject(settings);

    private void SaveAndClose()
    {
        configuration.Meter.ClassicWindow.Normalize(MeterSlotDefaults.CreateClassic());
        configuration.Meter.HorizontalWindow.Normalize(MeterSlotDefaults.CreateHorizontal());
        configuration.Meter.RoleSplitWindow.Normalize(MeterSlotDefaults.CreateRoleSplit());
        configuration.Meter.RoleSplitDamageWindow.Normalize(MeterSlotDefaults.CreateRoleSplit());
        configuration.Meter.RoleSplitHealerWindow.Normalize(MeterSlotDefaults.CreateRoleSplit());
        configuration.Meter.SynchronizeLegacyRoleSplitWindow();
        SynchronizeClassicSettings();
        SetEditingProfile(null);
        saveConfiguration();
        editingSnapshot = null;
        closeActionHandled = true;
        IsOpen = false;
    }

    private void CancelAndClose()
    {
        RestoreEditingSnapshot();
        SetEditingProfile(null);
        saveConfiguration();
        closeActionHandled = true;
        IsOpen = false;
    }

    private void CloseWithoutChanges()
    {
        SetEditingProfile(null);
        editingSnapshot = null;
        closeActionHandled = true;
        IsOpen = false;
    }

    private void RestoreEditingSnapshot()
    {
        if (string.IsNullOrWhiteSpace(editingSnapshot))
        {
            return;
        }

        var restored = JsonConvert.DeserializeObject<MeterSettings>(editingSnapshot);
        if (restored is not null)
        {
            configuration.Meter = restored;
        }
        editingSnapshot = null;
    }

    // A click in the preview selects the slot and shows its properties (所选槽位), like a click
    // on its row in the slot list.
    private MeterPreviewInteraction CreatePreviewInteraction(List<MeterSlotDefinition> slots)
        => new(
            slots,
            () => selectedSlotId,
            slotId =>
            {
                selectedSlotId = slotId;
                propertiesTab = 1;
            });

    private MeterPreviewInteraction CreateRoleSplitPreviewInteraction(RoleSplitGroup group)
        => new(
            RoleSplitProfile(group).Slots,
            () => selectedRoleSplitGroup == group ? selectedSlotId : null,
            slotId =>
            {
                SelectRoleSplitGroup(group, slotId);
                propertiesTab = 1;
            });

    private void ResetPreviewInteraction()
    {
        previewInteraction = CreatePreviewInteraction(CurrentSlots);
        roleSplitDamagePreviewInteraction = CreateRoleSplitPreviewInteraction(RoleSplitGroup.DamageTank);
        roleSplitHealerPreviewInteraction = CreateRoleSplitPreviewInteraction(RoleSplitGroup.Healer);
    }

    private MeterWindowProfile RoleSplitProfile(RoleSplitGroup group)
        => group == RoleSplitGroup.Healer
            ? configuration.Meter.RoleSplitHealerWindow
            : configuration.Meter.RoleSplitDamageWindow;

    private void SelectRoleSplitGroup(RoleSplitGroup group, string? slotId = null)
    {
        if (selectedRoleSplitGroup != group)
        {
            // A preview click must switch the property panels before selecting the
            // clicked slot; otherwise H looks interactive while still editing D/T.
            selectedRoleSplitGroup = group;
            selectedSlotId = null;
            EnsureSelectedSlot(RoleSplitProfile(group).Slots);
        }

        if (!string.IsNullOrWhiteSpace(slotId))
        {
            selectedSlotId = slotId;
        }
    }

    private static Encounter CreatePreviewEncounter(UiText text)
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
                index == 0 ? text.Get("自己", "You") : text.Format($"队友 {index:00}", $"Party member {index:00}"),
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
                HighestDamageAction: index % 2 == 0 ? text.Get("爆发击", "Burst strike") : text.Get("强力技能", "Powerful ability"),
                HighestDamage: 128_000 - (index * 1_700L),
                PartyGroup: (index / 8) + 1);
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        return new Encounter(
            Guid.Parse("7b2b7ff6-1a44-4d5e-8ec7-14053c7d2224"),
            now.AddMinutes(-3),
            null,
            text.Get("预览副本", "Preview duty"),
            text.Get("预览战斗", "Preview encounter"),
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

    private string CollapseDirectionLabel(MeterCollapseDirection direction)
        => direction == MeterCollapseDirection.Downward
            ? text.Get("向下", "Downward")
            : text.Get("向上", "Upward");

    private void SynchronizeClassicSettings()
    {
        var meter = configuration.Meter;
        var profile = meter.ClassicWindow;
        meter.IsLocked = profile.IsLocked;
        meter.ClickThroughWhenLocked = profile.ClickThroughWhenLocked;
        meter.AutoHideOutOfCombat = profile.AutoHideOutOfCombat;
        meter.AutoHideDelaySeconds = profile.AutoHideDelaySeconds;
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

    private void EnsureSelectedSlot(List<MeterSlotDefinition> slots)
    {
        if (!slots.Any(slot => string.Equals(
                slot.Id,
                selectedSlotId,
                StringComparison.OrdinalIgnoreCase)))
        {
            selectedSlotId = slots.FirstOrDefault()?.Id;
        }
    }

    private MeterSlotMetric FirstUnusedMetric(List<MeterSlotDefinition> slots)
        => MeterSlotDefaults.EditableMetrics.Where(CanUseMetric)
            .FirstOrDefault(metric => slots.All(slot => slot.Metric != metric));

    private void ReplaceCurrentSlots(List<MeterSlotDefinition> slots)
    {
        CurrentProfile.Slots = slots;
        ResetPreviewInteraction();
    }

    private void NormalizeCurrentSlots()
    {
        CurrentProfile.Normalize(DefaultSlots(selectedKind));
    }

    private bool CanUseMetric(MeterSlotMetric metric)
        => metric != MeterSlotMetric.Fflogs ||
           (selectedKind == MeterWindowKind.Classic && configuration.Fflogs.Enabled);

    private void SetEditingProfile(MeterWindowKind? kind)
    {
        configuration.Meter.ClassicWindow.IsEditing = kind == MeterWindowKind.Classic;
        configuration.Meter.HorizontalWindow.IsEditing = kind == MeterWindowKind.Horizontal;
        configuration.Meter.RoleSplitWindow.IsEditing = kind == MeterWindowKind.RoleSplit;
        configuration.Meter.RoleSplitDamageWindow.IsEditing = kind == MeterWindowKind.RoleSplit;
        configuration.Meter.RoleSplitHealerWindow.IsEditing = kind == MeterWindowKind.RoleSplit;
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
            MeterWindowKind.Horizontal => text.Get("横版", "Horizontal"),
            MeterWindowKind.RoleSplit => text.Get("职能分栏", "Role split"),
            _ => text.Get("经典榜", "Classic"),
        };

    private string KindDescription(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => text.Get(
                "横向滑动；内容按槽位顺序排布；背景颜色和不透明度可调整。",
                "Horizontal carousel; content follows slot order; background color and opacity are adjustable."),
            MeterWindowKind.RoleSplit => text.Get(
                "D/T 与治疗复用经典表格；左侧名称为文字，右上角按钮负责收起。",
                "D/T and healer reuse the classic table; the title is plain text and the top-right button collapses it."),
            _ => text.Get(
                "8 人本按槽位顺序显示表格列；24 人本使用固定紧凑条。",
                "The 8-player table follows slot order; the 24-player compact row is fixed."),
        };

}
