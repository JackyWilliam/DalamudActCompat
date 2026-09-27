namespace DalamudActCompat.Host;

internal static class FoxTtsLayoutCompatibility
{
    public static void Apply(Control pluginTab)
    {
        var page = pluginTab.Controls.Find("tabPageGeneralSettings", true).OfType<TabPage>().SingleOrDefault();
        if (page is null || page.Controls.ContainsKey("dactFoxTtsLayout")) return;
        Control? Find(string name) => page.Controls.Find(name, false).SingleOrDefault();
        var language = Find("tableLayoutPanelMainLanguage");
        var playback = Find("groupBoxPlayback");
        var preview = Find("groupBoxPreview");
        var update = Find("groupBoxUpdate");
        var integration = Find("groupBoxIntegration");
        var engine = Find("groupBoxTTSEngine");
        var settings = Find("panelTTSEngineSettings");
        if (language is null || playback is null || preview is null || update is null ||
            integration is null || engine is null || settings is null) return;

        // Upstream anchors the engine panel beyond a fixed sidebar. Reparent the
        // existing controls so their bindings survive and narrow/high-DPI windows can reflow.
        var sidebarWidth = playback.Width;
        var left = Stack(playback, preview, update, integration);
        var right = Stack(engine, settings);
        var layout = new TableLayoutPanel
        {
            Name = "dactFoxTtsLayout", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 3,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, sidebarWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        language.Dock = DockStyle.Top;
        layout.Controls.Add(language, 0, 0);
        layout.SetColumnSpan(language, 2);
        layout.Controls.Add(left, 0, 1);
        layout.Controls.Add(right, 1, 1);
        page.Controls.Add(layout);

        bool? stacked = null;
        void Reflow()
        {
            // Reserve room for labels and a usable slider instead of expanding the
            // window to upstream's design width. Scrolling is vertical on narrow screens.
            var narrow = page.ClientSize.Width < sidebarWidth * 2.3;
            if (stacked == narrow) return;
            stacked = narrow;
            layout.SuspendLayout();
            layout.ColumnStyles[0].SizeType = narrow ? SizeType.Percent : SizeType.Absolute;
            layout.ColumnStyles[0].Width = narrow ? 100 : sidebarWidth;
            layout.ColumnStyles[1].SizeType = narrow ? SizeType.Absolute : SizeType.Percent;
            layout.ColumnStyles[1].Width = narrow ? 0 : 100;
            layout.SetCellPosition(right, new TableLayoutPanelCellPosition(narrow ? 0 : 1, 1));
            layout.SetCellPosition(left, new TableLayoutPanelCellPosition(0, narrow ? 2 : 1));
            layout.SetColumnSpan(language, narrow ? 1 : 2);
            layout.ResumeLayout(true);
        }
        page.SizeChanged += (_, _) => Reflow();
        Reflow();
    }

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = controls.Length, Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in controls)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Top;
            stack.Controls.Add(control);
        }
        return stack;
    }
}
