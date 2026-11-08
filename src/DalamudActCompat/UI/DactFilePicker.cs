using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// DACT's own file and folder picker (安装 DLL / ZIP, Cactbot 安装 / 更新, 反馈截图, FFLogs 日志目录,
// ACT 扩展目录). Dalamud's picker kept its own layout and widgets whatever colours it was given
// (user: 「明显不适配 DACT 的风格」), so this one is drawn with the DACT kit in every skin: a 48 px
// header, common places and drives on the left, the folder's contents on the right with a
// clickable path above them, and 取消 / 选择 at the bottom.
internal sealed class DactFilePicker : Window
{
    private sealed record Entry(string Path, string Name, bool Folder, long Size, DateTime Modified);

    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly WindowDragController headerDrag = new();
    private readonly WindowFade fade = new();
    // The last folder per purpose, so 安装 DLL reopens where the previous DLL was picked.
    private readonly Dictionary<string, string> lastDirectories = new(StringComparer.Ordinal);
    private readonly List<string> selected = [];
    private string purpose = "";
    private string title = "";
    private string[] extensions = [];
    private bool folderMode;
    private int maxSelection = 1;
    private Action<bool, List<string>>? done;
    // "" is 此电脑 (the drive list).
    private string directory = "";
    private List<Entry> entries = [];
    private string? listError;
    private string pathDraft = "";
    private bool editingPath;
    private bool focusPath;
    private bool resetScroll;
    private bool stylePushed;

    public DactFilePicker(UiText text, ISharedImmediateTexture logoTexture)
        : base("DACT file picker###DalamudActCompatFilePicker",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings)
    {
        this.text = text;
        this.logoTexture = logoTexture;
        Size = new Vector2(780, 520);
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620, 400), MaximumSize = new Vector2(float.MaxValue) };
        ShowCloseButton = false;
        // Dalamud hands Esc to ImGui only while a text field is typing; its own close hotkey
        // closes the window, and OnClose answers the caller with 取消.
        RespectCloseHotkey = true;
    }

    // extensions: lower-case with the dot (".dll"). maxSelection > 1 lets a click toggle several.
    public void OpenFile(string purpose, string title, string[] extensions, Action<bool, List<string>> done, int maxSelection = 1,
        string? startDirectory = null)
        => Open(purpose, title, extensions, false, maxSelection, done, startDirectory);

    public void OpenFolder(string purpose, string title, Action<bool, string> done, string? startDirectory = null)
        => Open(purpose, title, [], true, 1, (ok, paths) => done(ok && paths.Count > 0, paths.FirstOrDefault() ?? string.Empty), startDirectory);

    private void Open(string purpose, string title, string[] extensions, bool folderMode, int maxSelection, Action<bool, List<string>> done,
        string? startDirectory)
    {
        // A picker still open for something else is answered 取消 first: one caller, one answer.
        Finish(false);
        this.purpose = purpose;
        this.title = title;
        this.extensions = extensions.Select(extension => extension.ToLowerInvariant()).ToArray();
        this.folderMode = folderMode;
        this.maxSelection = Math.Max(1, maxSelection);
        this.done = done;
        var start = lastDirectories.GetValueOrDefault(purpose) ?? startDirectory;
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
            start = folderMode ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : DownloadsFolder();
        Navigate(start);
        editingPath = false;
        IsOpen = true;
        BringToFront();
    }

    private static string DownloadsFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private void Finish(bool accepted)
    {
        if (done is not { } callback) return;
        done = null;
        var paths = accepted ? selected.ToList() : [];
        if (accepted && directory.Length > 0) lastDirectories[purpose] = directory;
        selected.Clear();
        fade.RequestClose();
        callback(accepted, paths);
    }

    public override void OnClose() => Finish(false);

    public override void OnOpen() => fade.Opened();

    // ---- Listing

    private void Navigate(string target)
    {
        listError = null;
        selected.Clear();
        resetScroll = true;
        if (target.Length == 0)
        {
            directory = "";
            entries = Drives().ToList();
            return;
        }
        try
        {
            var full = Path.GetFullPath(target);
            var info = new DirectoryInfo(full);
            var list = new List<Entry>();
            foreach (var folder in info.EnumerateDirectories())
            {
                if (Hidden(folder.Attributes)) continue;
                list.Add(new Entry(folder.FullName, folder.Name, true, 0, folder.LastWriteTime));
            }
            if (!folderMode)
            {
                foreach (var file in info.EnumerateFiles())
                {
                    if (Hidden(file.Attributes) || extensions.Length > 0 && !extensions.Contains(file.Extension.ToLowerInvariant())) continue;
                    list.Add(new Entry(file.FullName, file.Name, false, file.Length, file.LastWriteTime));
                }
            }
            var order = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
            entries = list.OrderBy(entry => entry.Folder ? 0 : 1).ThenBy(entry => entry.Name, order).ToList();
            directory = full;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // The folder stays where it was; the reason shows over the list.
            listError = text.Format($"无法打开这个文件夹：{error.Message}", $"Can't open this folder: {error.Message}");
        }
    }

    private static bool Hidden(FileAttributes attributes) => (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static IEnumerable<Entry> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Unready (empty card readers) and network drives can stall the frame; local ones only.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
            var letter = drive.Name.TrimEnd('\\');
            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{drive.VolumeLabel} ({letter})";
            yield return new Entry(drive.RootDirectory.FullName, label, true, 0, default);
        }
    }

    // ---- Window

    public override void PreDraw()
    {
        // Centred when it appears; the drag goes second, because the later SetNextWindowPos
        // wins and the centring one (written after it) kept every drag from moving the window.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * .5f, ImGuiCond.Appearing, new Vector2(.5f));
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        stylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
        if (!fade.Push()) IsOpen = false;
    }

    public override void PostDraw()
    {
        fade.Pop();
        if (!stylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        stylePushed = false;
    }

    public override void Draw()
    {
        ControlCenterWindow.PushTheme();
        try
        {
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            DactUi.HostMin = min;
            DactUi.HostMax = min + size;
            var line = Math.Max(1, s);
            var draw = ImGui.GetWindowDrawList();
            DrawHeader(min, size.X, 48 * s);
            draw.AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line), DactUi.U32(DactUi.DividerColor));
            var footer = 60 * s;
            var bodyTop = min.Y + 48 * s + line;
            var bodyHeight = size.Y - (bodyTop - min.Y) - footer;
            ImGui.SetCursorScreenPos(new Vector2(min.X, bodyTop));
            DrawPlaces(new Vector2(176 * s, bodyHeight));
            ImGui.SetCursorScreenPos(new Vector2(min.X + 176 * s, bodyTop));
            DrawFolder(new Vector2(size.X - 176 * s, bodyHeight));
            draw.AddRectFilled(new Vector2(min.X, min.Y + size.Y - footer), new Vector2(min.X + size.X, min.Y + size.Y - footer + line), DactUi.U32(DactUi.DividerColor));
            DrawFooter(new Vector2(min.X, min.Y + size.Y - footer), size.X, footer);
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    private void DrawHeader(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var x = logo.X + 36 * s;
        var right = min.X + width - 10 * s - 28 * s;
        var shown = DactUi.Fit(title, right - 12 * s - x);
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, shown, DactTheme.Palette.Text);
        var dragLeft = x + DactUi.TextWidth(shown) + 8 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (height - 28 * s) * .5f));
        if (DactUi.IconButton("##picker-close", "close", text.Get("取消", "Cancel"))) Finish(false);
        if (right - 8 * s > dragLeft)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragLeft, min.Y));
            ImGui.InvisibleButton("##picker-drag", new Vector2(right - 8 * s - dragLeft, height));
            headerDrag.HandleItem();
        }
    }

    // Left column: the usual places, then the drives.
    private void DrawPlaces(Vector2 size)
    {
        var s = DactUi.S;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 12) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##picker-places", size, false, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            var places = new List<(string Icon, string Label, string Path)>
            {
                ("home", text.Get("桌面", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                ("download", text.Get("下载", "Downloads"), DownloadsFolder()),
                ("copy", text.Get("文档", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("palette", text.Get("图片", "Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("layers", text.Get("此电脑", "This PC"), ""),
            };
            foreach (var place in places.Where(place => place.Path.Length == 0 || Directory.Exists(place.Path)))
                if (PlaceRow(place.Icon, place.Label, place.Path)) Navigate(place.Path);
            DactUi.Space(6);
            DactUi.Divider();
            DactUi.Space(6);
            foreach (var drive in Drives())
                if (PlaceRow("folder", drive.Name, drive.Path)) Navigate(drive.Path);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    private bool PlaceRow(string icon, string label, string path)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 32 * s;
        var clicked = ImGui.InvisibleButton("##place-" + label + path, new Vector2(width, height));
        var min = ImGui.GetItemRectMin();
        var hovered = ImGui.IsItemHovered();
        var current = string.Equals(path.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var fill = DactMotion.Color(ImGuiP.GetItemID(), palette.Hover with { W = current ? 1 : hovered ? .55f : 0 }, DactMotion.Fast);
        var draw = ImGui.GetWindowDrawList();
        if (fill.W > 0) draw.AddRectFilled(min, min + new Vector2(width, height), DactUi.U32(fill), 7 * s);
        var ink = current ? palette.Accent : hovered ? palette.Text : palette.Muted;
        DactIcons.Draw(draw, icon, min + new Vector2(10 * s, (height - 16 * s) * .5f), 16 * s, ink);
        DactUi.DrawText(draw, min + new Vector2(34 * s, 0), height, DactUi.Fit(label, width - 40 * s), current || hovered ? palette.Text : palette.Muted);
        return clicked;
    }

    // Right side: the path bar and the folder's contents.
    private void DrawFolder(Vector2 size)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12) * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##picker-folder", size, false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 10) * s);
            DrawPathBar();
            if (listError is { } error) DactUi.Wrapped(error, DactUi.Danger, DactFonts.Small);
            DrawEntries();
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawPathBar()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var width = ImGui.GetContentRegionAvail().X;
        var up = directory.Length == 0 ? null : Directory.GetParent(directory)?.FullName ?? "";
        ImGui.BeginDisabled(up is null);
        if (DactUi.IconButton("##picker-up", "chevronUp", text.Get("上一级", "Up one level")) && up is not null) Navigate(up);
        ImGui.EndDisabled();
        ImGui.SameLine(0, 6 * s);
        var barMin = ImGui.GetCursorScreenPos();
        var barWidth = width - 28 * s - 6 * s - 6 * s - 28 * s;
        var draw = ImGui.GetWindowDrawList();
        if (editingPath)
        {
            if (focusPath)
            {
                ImGui.SetKeyboardFocusHere();
                focusPath = false;
            }
            // Esc is read before the field takes it for the frame.
            var cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);
            DactUi.Input("picker-path", ref pathDraft, text.Get("输入文件夹路径，按回车前往", "Type a folder path; press Enter to go"), 1024, width: barWidth);
            if (cancel) editingPath = false;
            else if (ImGui.IsItemDeactivated())
            {
                editingPath = false;
                if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) Navigate(pathDraft.Trim().Trim('"'));
            }
        }
        else
        {
            // The path as segments, each one a step back up; the deepest ones win when it is long.
            draw.AddRectFilled(barMin, barMin + new Vector2(barWidth, 30 * s), DactUi.U32(DactUi.Field), 6 * s);
            draw.AddRect(barMin, barMin + new Vector2(barWidth, 30 * s), DactUi.U32(DactUi.FieldBorder), 6 * s, ImDrawFlags.None, Math.Max(1, s));
            var segments = Segments();
            var widths = segments.Select(segment => DactUi.TextWidth(segment.Label) + 12 * s).ToArray();
            var room = barWidth - 12 * s;
            var first = segments.Count - 1;
            var used = widths[first];
            while (first > 0 && used + 18 * s + widths[first - 1] <= room - 22 * s)
            {
                first--;
                used += 18 * s + widths[first];
            }
            var x = barMin.X + 6 * s;
            if (first > 0)
            {
                DactUi.DrawText(draw, new Vector2(x, barMin.Y), 30 * s, "…", palette.Muted);
                x += 22 * s;
            }
            for (var i = first; i < segments.Count; i++)
            {
                if (i > first)
                {
                    DactIcons.Draw(draw, "chevronRight", new Vector2(x + 2 * s, barMin.Y + 9 * s), 12 * s, palette.Muted);
                    x += 18 * s;
                }
                ImGui.SetCursorScreenPos(new Vector2(x, barMin.Y + 3 * s));
                var segmentWidth = Math.Min(widths[i], barMin.X + barWidth - 6 * s - x);
                if (segmentWidth <= 0) break;
                if (ImGui.InvisibleButton("##picker-segment-" + i, new Vector2(segmentWidth, 24 * s)) && i < segments.Count - 1) Navigate(segments[i].Path);
                var hovered = ImGui.IsItemHovered();
                var fill = DactMotion.Color(ImGuiP.GetItemID(), palette.Hover with { W = hovered ? 1 : 0 }, DactMotion.Fast);
                if (fill.W > 0) draw.AddRectFilled(new Vector2(x, barMin.Y + 3 * s), new Vector2(x + segmentWidth, barMin.Y + 27 * s), DactUi.U32(fill), 5 * s);
                DactUi.DrawText(draw, new Vector2(x + 6 * s, barMin.Y), 30 * s, DactUi.Fit(segments[i].Label, segmentWidth - 10 * s),
                    i == segments.Count - 1 ? palette.Text : palette.Muted);
                x += segmentWidth;
            }
            ImGui.SetCursorScreenPos(barMin);
            ImGui.Dummy(new Vector2(barWidth, 30 * s));
        }
        ImGui.SameLine(0, 6 * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, barMin.Y + 1 * s));
        if (DactUi.IconButton("##picker-edit", "edit", text.Get("输入路径", "Type a path")))
        {
            editingPath = !editingPath;
            pathDraft = directory;
            focusPath = editingPath;
        }
    }

    private List<(string Label, string Path)> Segments()
    {
        var segments = new List<(string Label, string Path)> { (text.Get("此电脑", "This PC"), "") };
        if (directory.Length == 0) return segments;
        var root = Path.GetPathRoot(directory) ?? directory;
        segments.Add((root.TrimEnd('\\'), root));
        var current = root;
        foreach (var part in directory[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add((part, current));
        }
        return segments;
    }

    // Rows of 32 px; only the visible ones are drawn, so a large Downloads folder stays cheap.
    private void DrawEntries()
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var size = ImGui.GetContentRegionAvail();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Math.Max(1, s));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, DactUi.Field with { W = DactUi.Field.W * .6f });
        ImGui.PushStyleColor(ImGuiCol.Border, DactUi.FieldBorder);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        if (ImGui.BeginChild("##picker-entries", size, true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            if (resetScroll)
            {
                ImGui.SetScrollY(0);
                resetScroll = false;
            }
            var width = ImGui.GetContentRegionAvail().X;
            var rowHeight = 32 * s;
            if (entries.Count == 0)
            {
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(12, 10) * s);
                var empty = folderMode || extensions.Length == 0
                    ? text.Get("这个文件夹是空的。", "This folder is empty.")
                    : text.Format($"这里没有可选的文件（只显示 {string.Join(" ", extensions)}）。",
                        $"No files to pick here (only {string.Join(" ", extensions)} are shown).");
                DactUi.Wrapped(empty, palette.Muted, DactFonts.Small, width - 24 * s);
            }
            else
            {
                var top = ImGui.GetCursorPosY();
                var scroll = ImGui.GetScrollY();
                var first = Math.Max(0, (int)((scroll - top) / rowHeight) - 1);
                var last = Math.Min(entries.Count, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);
                Entry? open = null;
                for (var index = first; index < last; index++)
                {
                    ImGui.SetCursorPosY(top + index * rowHeight);
                    if (EntryRow(entries[index], width, rowHeight)) open = entries[index];
                }
                ImGui.SetCursorPosY(top + entries.Count * rowHeight);
                ImGui.Dummy(new Vector2(width, 0));
                if (open is { Folder: true }) Navigate(open.Path);
                else if (open is not null)
                {
                    selected.Clear();
                    selected.Add(open.Path);
                    Finish(true);
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(5);
    }

    // One row: a click selects, a double-click opens the folder or picks the file. Returns true
    // on the double-click.
    private bool EntryRow(Entry entry, float width, float height)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##entry-" + entry.Path, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var opened = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (clicked) Toggle(entry);
        var picked = selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase);
        var fill = DactMotion.Color(ImGuiP.GetItemID(), picked ? DactUi.AccentSoft with { W = .22f } : palette.Hover with { W = hovered ? .6f : 0 },
            DactMotion.Fast);
        var draw = ImGui.GetWindowDrawList();
        if (fill.W > 0) draw.AddRectFilled(min, min + new Vector2(width, height), DactUi.U32(fill), 6 * s);
        var icon = entry.Folder ? "folder" : Path.GetExtension(entry.Path).ToLowerInvariant() switch
        {
            ".dll" => "plug",
            ".zip" => "download",
            ".png" or ".jpg" or ".jpeg" => "palette",
            _ => "copy",
        };
        DactIcons.Draw(draw, icon, min + new Vector2(10 * s, (height - 16 * s) * .5f), 16 * s, entry.Folder ? palette.Gold : picked ? palette.Accent : palette.Muted);
        var detailWidth = 0f;
        if (entry.Modified != default)
        {
            var detail = entry.Folder
                ? entry.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : $"{FormatSize(entry.Size)}   {entry.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
            detailWidth = DactUi.TextWidth(detail, DactFonts.Small) + 12 * s;
            DactUi.DrawText(draw, new Vector2(min.X + width - detailWidth, min.Y), height, detail, DactUi.HintColor, DactFonts.Small);
        }
        DactUi.DrawText(draw, min + new Vector2(36 * s, 0), height, DactUi.Fit(entry.Name, width - 48 * s - detailWidth), picked ? palette.Accent : palette.Text);
        if (picked && !entry.Folder) DactIcons.Draw(draw, "check", new Vector2(min.X + width - detailWidth - 22 * s, min.Y + (height - 14 * s) * .5f), 14 * s, palette.Accent);
        return opened;
    }

    private void Toggle(Entry entry)
    {
        // Folders are only selected in folder mode; in file mode a click on one does nothing until
        // the double-click opens it.
        if (entry.Folder != folderMode) return;
        var index = selected.FindIndex(path => string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            selected.RemoveAt(index);
            return;
        }
        if (maxSelection == 1) selected.Clear();
        if (selected.Count < maxSelection) selected.Add(entry.Path);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        _ => $"{bytes / 1024d / 1024 / 1024:0.#} GB",
    };

    private void DrawFooter(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var draw = ImGui.GetWindowDrawList();
        var cancel = text.Get("取消", "Cancel");
        // Folder mode picks the highlighted folder, or the one being shown when none is.
        var choose = folderMode && selected.Count == 0 ? text.Get("选择当前文件夹", "Select this folder") : text.Get("选择", "Select");
        var chooseWidth = DactUi.ButtonWidth(choose, ButtonKind.Primary);
        var cancelWidth = DactUi.ButtonWidth(cancel);
        var buttonsLeft = min.X + width - 18 * s - chooseWidth - 8 * s - cancelWidth;
        var summary = selected.Count > 0
            ? string.Join("、", selected.Select(Path.GetFileName))
            : folderMode
                ? directory.Length == 0 ? text.Get("先打开一个磁盘或文件夹", "Open a drive or folder first") : directory
                : maxSelection > 1
                    ? text.Format($"单击选择，最多 {maxSelection} 个；双击打开文件夹", $"Click to select, up to {maxSelection}; double-click opens a folder")
                    : text.Get("单击选择文件，双击直接选中；双击文件夹打开", "Click a file to select it, double-click to pick it; double-click a folder to open it");
        var summaryColor = selected.Count > 0 ? palette.Text : DactUi.HintColor;
        DactUi.DrawText(draw, new Vector2(min.X + 18 * s, min.Y), height, DactUi.Fit(summary, buttonsLeft - min.X - 30 * s), summaryColor,
            selected.Count > 0 ? DactFonts.Body : DactFonts.Small);
        ImGui.SetCursorScreenPos(new Vector2(buttonsLeft, min.Y + (height - 30 * s) * .5f));
        if (DactUi.Button(cancel + "##picker-cancel")) Finish(false);
        ImGui.SameLine(0, 8 * s);
        var canChoose = selected.Count > 0 || folderMode && directory.Length > 0;
        if (DactUi.Button(choose + "##picker-choose", ButtonKind.Primary, disabled: !canChoose))
        {
            if (folderMode && selected.Count == 0) selected.Add(directory);
            Finish(true);
        }
    }
}
