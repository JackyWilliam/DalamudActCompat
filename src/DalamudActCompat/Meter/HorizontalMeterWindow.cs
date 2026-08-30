using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

public sealed class HorizontalMeterWindow : Window
{
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 Muted = new(0.68f, 0.72f, 0.77f, 1);
    private readonly MeterService meterService;
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly JobIconTextureSet jobIcons;
    private readonly Action saveConfiguration;
    private float scrollOffset;
    private bool locateOnNextDraw;
    private long locatePreviewExpiresAt;

    public HorizontalMeterWindow(
        MeterService meterService,
        PluginConfiguration configuration,
        UiText text,
        JobIconTextureSet jobIcons,
        Action saveConfiguration)
        : base("横版战斗统计###DalamudActCompatHorizontalMeter")
    {
        this.meterService = meterService;
        this.configuration = configuration;
        this.text = text;
        this.jobIcons = jobIcons;
        this.saveConfiguration = saveConfiguration;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(1280, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 90),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private MeterWindowProfile Profile => configuration.Meter.HorizontalWindow;

    public override bool DrawConditions()
    {
        WindowName = text.Get(
            "横版战斗统计###DalamudActCompatHorizontalMeter",
            "Horizontal Combat Meter###DalamudActCompatHorizontalMeter");
        if (!configuration.Meter.IsVisible || !Profile.IsEnabled)
        {
            return false;
        }

        var encounter = meterService.DisplayEncounter;
        return Environment.TickCount64 < locatePreviewExpiresAt ||
               !Profile.AutoHideOutOfCombat ||
               encounter?.IsActive == true;
    }

    public void LocateOnNextDraw()
    {
        locateOnNextDraw = true;
        locatePreviewExpiresAt = Environment.TickCount64 + 3_000;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        if (locateOnNextDraw)
        {
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size * 0.42f),
                ImGuiCond.Always,
                new Vector2(0.5f));
            locateOnNextDraw = false;
        }
        else
        {
            ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(
                new Vector2(viewport.Size.X, Math.Max(120, viewport.Size.Y / 3)),
                ImGuiCond.FirstUseEver);
        }

        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;
        if (!Profile.IsEditing)
        {
            // Runtime layout has no resize grip; resizing is an explicit editor action.
            Flags |= ImGuiWindowFlags.NoResize;
        }
        if (Profile.IsLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        }
        if (Profile.IsLocked && Profile.ClickThroughWhenLocked)
        {
            Flags |= ImGuiWindowFlags.NoInputs;
        }

        // The horizontal meter must remain compositable over the game. Every style
        // color capable of producing a black block is explicitly transparent.
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 4));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(4);
    }

    public override void Draw()
    {
        using var fontScale = new MeterFontScaleScope(Profile.FontScale);
        var encounter = meterService.DisplayEncounter;
        if (encounter is null)
        {
            DrawHeader(null, []);
            ImGui.TextColored(Muted, text.Get("等待战斗数据…", "Waiting for encounter data…"));
            return;
        }

        var allRows = MeterSlotPresentation.SortAndRank(
            meterService.GetRows(encounter),
            Profile.SortMode,
            Profile.DpsSortMetric);
        var partyGroup = ResolvePartyGroup(encounter, allRows);
        DrawHeader(encounter, allRows);
        var rows = MeterSlotPresentation.SelectParty(allRows, partyGroup);
        if (rows.Count == 0)
        {
            ImGui.TextColored(Muted, text.Get("等待玩家数据…", "Waiting for player data…"));
            return;
        }

        DrawSlidingPlayers(encounter, rows);
        MeterSlotPresentation.DrawTeamSummary(
            "horizontal",
            encounter,
            Profile.Slots,
            text,
            Muted,
            Gold);
    }

    private void DrawHeader(
        Encounter? encounter,
        IReadOnlyList<CombatantRow> rows,
        bool embeddedPreview = false)
    {
        var start = ImGui.GetCursorScreenPos();
        var lineHeight = Math.Max(20, ImGui.GetTextLineHeight() + 4);
        var dpsWidth = DrawModeButton(
            "DPS 榜",
            MeterSortMode.Dps,
            start,
            lineHeight,
            persistChanges: !embeddedPreview);
        var hpsStart = start + new Vector2(dpsWidth + 10, 0);
        var hpsWidth = DrawModeButton(
            "HPS 榜",
            MeterSortMode.Hps,
            hpsStart,
            lineHeight,
            persistChanges: !embeddedPreview);
        var controlsEnd = hpsStart.X + hpsWidth + 14;
        if (encounter is not null && MeterSlotPresentation.IsAlliance(encounter, rows))
        {
            controlsEnd = DrawPartyButtons(
                rows,
                controlsEnd,
                start.Y,
                lineHeight,
                persistChanges: !embeddedPreview) + 12;
        }
        var dragStart = new Vector2(controlsEnd, start.Y);
        var dragWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - (dragStart.X - start.X));
        ImGui.SetCursorScreenPos(dragStart);
        ImGui.InvisibleButton("horizontal-meter-drag", new Vector2(dragWidth, lineHeight));
        if (!embeddedPreview && !Profile.IsLocked && ImGui.IsItemActive() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }

        ImGui.GetWindowDrawList().AddText(
            dragStart,
            ImGui.GetColorU32(new Vector4(Muted.X, Muted.Y, Muted.Z, 0.62f)),
            Profile.IsLocked ? text.Get("已锁定", "Locked") : text.Get("拖动窗口", "Drag window"));
        ImGui.SetCursorScreenPos(start + new Vector2(0, lineHeight + 3));
    }

    internal void DrawEditorPreview(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterPreviewInteraction previewInteraction)
    {
        using var fontScale = new MeterFontScaleScope(Profile.FontScale);
        var ranked = MeterSlotPresentation.SortAndRank(
            rows,
            Profile.SortMode,
            Profile.DpsSortMetric);
        var partyGroup = ResolvePartyGroup(encounter, ranked, persistChanges: false);
        DrawHeader(encounter, ranked, embeddedPreview: true);
        DrawSlidingPlayers(
            encounter,
            MeterSlotPresentation.SelectParty(ranked, partyGroup),
            previewInteraction);
        MeterSlotPresentation.DrawTeamSummary(
            "horizontal-editor-preview",
            encounter,
            Profile.Slots,
            text,
            Muted,
            Gold,
            previewInteraction);
    }

    private float DrawPartyButtons(
        IReadOnlyList<CombatantRow> rows,
        float startX,
        float startY,
        float height,
        bool persistChanges = true)
    {
        var x = startX;
        for (var group = 1; group <= 3; group++)
        {
            var label = ((char)('A' + group - 1)).ToString();
            var width = ImGui.CalcTextSize(label).X + 14;
            ImGui.SetCursorScreenPos(new Vector2(x, startY));
            ImGui.InvisibleButton($"horizontal-party-{group}", new Vector2(width, height));
            var selected = configuration.Meter.HorizontalPartyGroup == group;
            var hovered = ImGui.IsItemHovered();
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRect(
                new Vector2(x, startY),
                new Vector2(x + width, startY + height),
                ImGui.GetColorU32(selected ? Gold : hovered ? IceBlue : Muted),
                4);
            var textSize = ImGui.CalcTextSize(label);
            drawList.AddText(
                new Vector2(x + ((width - textSize.X) * 0.5f), startY + 1),
                ImGui.GetColorU32(selected ? Gold : hovered ? IceBlue : Muted),
                label);
            if (ImGui.IsItemClicked() && rows.Any(row => row.PartyGroup == group))
            {
                configuration.Meter.HorizontalPartyGroup = group;
                scrollOffset = 0;
                if (persistChanges)
                {
                    saveConfiguration();
                }
            }
            x += width + 5;
        }
        return x;
    }

    private int ResolvePartyGroup(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        bool persistChanges = true)
    {
        if (!MeterSlotPresentation.IsAlliance(encounter, rows))
        {
            return 0;
        }

        var availableGroups = rows.Select(static row => row.PartyGroup)
            .Where(static group => group is > 0 and <= 3)
            .Distinct()
            .ToArray();
        var selected = configuration.Meter.HorizontalPartyGroup;
        if (!availableGroups.Contains(selected))
        {
            selected = MeterSlotPresentation.ResolveLocalPartyGroup(rows);
            if (!availableGroups.Contains(selected))
            {
                selected = availableGroups.FirstOrDefault();
            }
            if (configuration.Meter.HorizontalPartyGroup != selected)
            {
                configuration.Meter.HorizontalPartyGroup = selected;
                if (persistChanges)
                {
                    saveConfiguration();
                }
            }
        }
        return selected;
    }

    private float DrawModeButton(
        string label,
        MeterSortMode mode,
        Vector2 start,
        float height,
        bool persistChanges = true)
    {
        var width = ImGui.CalcTextSize(label).X + 4;
        ImGui.SetCursorScreenPos(start);
        ImGui.InvisibleButton($"horizontal-mode-{mode}", new Vector2(width, height));
        var selected = MeterSortModeOptions.Normalize(Profile.SortMode) == mode;
        var hovered = ImGui.IsItemHovered();
        ImGui.GetWindowDrawList().AddText(
            start + new Vector2(2, 1),
            ImGui.GetColorU32(selected ? Gold : hovered ? IceBlue : Muted),
            label);
        if (ImGui.IsItemClicked())
        {
            Profile.SortMode = mode;
            configuration.Meter.SortMode = mode;
            MeterSlotPresentation.ReplacePrimaryMetric(Profile, mode);
            if (persistChanges)
            {
                saveConfiguration();
            }
        }

        return width;
    }

    private void DrawSlidingPlayers(
        Encounter encounter,
        IReadOnlyList<CombatantRow> rows,
        MeterPreviewInteraction? previewInteraction = null)
    {
        var slots = Profile.Slots.Where(static slot => slot.Visible).ToArray();
        var placements = BuildSlotPlacements(slots);
        const float slotRowHeight = 26;
        var slotRows = placements.Select(static placement => placement.Row)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var cardHeight = Math.Max(28, slotRows * slotRowHeight);
        var available = ImGui.GetContentRegionAvail();
        var summaryReserve = MeterSlotPresentation.HasTeamSummary(slots)
            ? MeterSlotPresentation.TeamSummaryHeight + 4
            : 0;
        var bodySize = new Vector2(
            Math.Max(1, available.X),
            Math.Max(cardHeight, available.Y - summaryReserve));
        var bodyStart = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("horizontal-player-slider", bodySize);

        const float spacing = 18;
        var itemWidth = Profile.ItemWidth;
        var contentWidth = (rows.Count * itemWidth) + (Math.Max(0, rows.Count - 1) * spacing);
        var maximumOffset = Math.Max(0, contentWidth - bodySize.X);
        if (ImGui.IsItemHovered() && !(Profile.IsLocked && Profile.ClickThroughWhenLocked))
        {
            scrollOffset -= ImGui.GetIO().MouseWheel * 64;
        }
        if (previewInteraction is null && ImGui.IsItemActive() &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // Content follows the pointer, matching a watch complication carousel.
            scrollOffset -= ImGui.GetIO().MouseDelta.X;
        }
        scrollOffset = Math.Clamp(scrollOffset, 0, maximumOffset);

        var drawList = ImGui.GetWindowDrawList();
        var bodyEnd = bodyStart + bodySize;
        drawList.PushClipRect(bodyStart, bodyEnd, true);
        for (var index = 0; index < rows.Count; index++)
        {
            var cardStart = bodyStart + new Vector2(
                (index * (itemWidth + spacing)) - scrollOffset,
                1);
            if (cardStart.X + itemWidth < bodyStart.X || cardStart.X > bodyEnd.X)
            {
                continue;
            }

            DrawPlayer(
                encounter,
                rows[index],
                placements,
                cardStart,
                itemWidth,
                cardHeight,
                previewInteraction);
        }
        drawList.PopClipRect();
    }

    private void DrawPlayer(
        Encounter encounter,
        CombatantRow row,
        IReadOnlyList<HorizontalSlotPlacement> placements,
        Vector2 start,
        float width,
        float height,
        MeterPreviewInteraction? previewInteraction)
    {
        var drawList = ImGui.GetWindowDrawList();
        var displayName = MeterSlotPresentation.DisplayName(row, encounter, configuration.Meter, text);
        const float slotRowHeight = 26;
        foreach (var placement in placements)
        {
            var cellWidth = width * 0.5f;
            var slotWidth = cellWidth * placement.ColumnSpan;
            var slotStart = start + new Vector2(
                placement.Column * cellWidth,
                placement.Row * slotRowHeight);
            var slotEnd = slotStart + new Vector2(slotWidth, slotRowHeight - 2);
            switch (placement.Slot.Metric)
            {
                case MeterSlotMetric.PlayerIdentity:
                    var cursorX = slotStart.X + DrawJob(row, slotStart, 22) + 5;
                    drawList.AddText(
                        new Vector2(cursorX, slotStart.Y + 3),
                        ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One),
                        MeterSlotPresentation.TrimToWidth(
                            displayName,
                            Math.Max(16, slotEnd.X - cursorX - 4)));
                    break;
                case MeterSlotMetric.Job:
                    DrawJob(row, slotStart, 22);
                    break;
                case MeterSlotMetric.PlayerName:
                    drawList.AddText(
                        slotStart + new Vector2(0, 3),
                        ImGui.GetColorU32(row.IsLocalPlayer ? Gold : Vector4.One),
                        MeterSlotPresentation.TrimToWidth(displayName, Math.Max(16, slotWidth - 4)));
                    break;
                default:
                    var label = MeterSlotPresentation.Label(placement.Slot.Metric, text);
                    var value = MeterSlotPresentation.Value(placement.Slot.Metric, row, displayName);
                    var labelWidth = Math.Min(slotWidth * 0.47f, ImGui.CalcTextSize(label).X + 6);
                    drawList.AddText(
                        slotStart + new Vector2(0, 3),
                        ImGui.GetColorU32(Muted),
                        MeterSlotPresentation.TrimToWidth(label, labelWidth));
                    drawList.AddText(
                        slotStart + new Vector2(labelWidth, 3),
                        ImGui.GetColorU32(PrimaryColor(placement.Slot.Metric, row.IsLocalPlayer)),
                        MeterSlotPresentation.TrimToWidth(
                            value,
                            Math.Max(10, slotWidth - labelWidth - 5)));
                    break;
            }

            previewInteraction?.Observe(
                placement.Slot,
                slotStart,
                slotEnd,
                drawList,
                highlightSelection: row.IsLocalPlayer);
        }

        drawList.AddLine(
            start + new Vector2(width + 8, 1),
            start + new Vector2(width + 8, height - 2),
            ImGui.GetColorU32(new Vector4(IceBlue.X, IceBlue.Y, IceBlue.Z, 0.35f)));
        if (ImGui.IsMouseHoveringRect(start, start + new Vector2(width, height)) && row.HighestDamage > 0)
        {
            ImGui.SetTooltip(text.Get(
                $"最高单次：{row.HighestDamageAction} {row.HighestDamage:N0}",
                $"Highest hit: {row.HighestDamageAction} {row.HighestDamage:N0}"));
        }
    }

    private static IReadOnlyList<HorizontalSlotPlacement> BuildSlotPlacements(
        IReadOnlyList<MeterSlotDefinition> slots)
    {
        var placements = new List<HorizontalSlotPlacement>();
        var row = 0;
        var column = 0;
        foreach (var slot in slots.Where(static slot =>
                     slot.Metric is not MeterSlotMetric.TotalDamage and
                     not MeterSlotMetric.TotalHealing and
                     not MeterSlotMetric.Fflogs))
        {
            var fullWidth = slot.Metric is MeterSlotMetric.PlayerIdentity or
                MeterSlotMetric.Job or MeterSlotMetric.PlayerName;
            if (fullWidth && column != 0)
            {
                row++;
                column = 0;
            }

            placements.Add(new HorizontalSlotPlacement(slot, row, column, fullWidth ? 2 : 1));
            if (fullWidth || ++column >= 2)
            {
                row++;
                column = 0;
            }
        }

        return placements;
    }

    private float DrawJob(CombatantRow row, Vector2 start, float size)
    {
        var texture = MeterService.IsLimitBreak(row.Id, row.Name)
            ? jobIcons.GetLimitBreak()
            : jobIcons.Get(configuration.Meter.JobDisplayStyle, row.Job);
        if (texture is not null)
        {
            ImGui.GetWindowDrawList().AddImage(
                texture.GetWrapOrEmpty().Handle,
                start,
                start + new Vector2(size));
            return size;
        }

        var job = JobDisplayFormatter.FormatText(row.Job, configuration.Meter.JobDisplayStyle);
        ImGui.GetWindowDrawList().AddText(start + new Vector2(0, 3), ImGui.GetColorU32(IceBlue), job);
        return ImGui.CalcTextSize(job).X;
    }

    private static Vector4 PrimaryColor(MeterSlotMetric metric, bool isLocalPlayer)
        => isLocalPlayer
            ? Gold
            : metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps or
                MeterSlotMetric.EncDps or MeterSlotMetric.ExtDps or MeterSlotMetric.Hps
                ? IceBlue
                : Vector4.One;

    private sealed record HorizontalSlotPlacement(
        MeterSlotDefinition Slot,
        int Row,
        int Column,
        int ColumnSpan);
}
