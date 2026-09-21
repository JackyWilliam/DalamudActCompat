using System.Drawing;
using System.Windows.Forms;
using RainbowMage.OverlayPlugin.EventSources;

namespace DalamudActCompat.ActRuntime;

internal sealed class CactbotDirectoryPicker(Func<string, string> choose) : ICactbotDirectoryPicker
{
    public string ChooseDirectory(string currentDirectory) => choose(currentDirectory);

    internal static string Show(IWin32Window owner, string currentDirectory)
    {
        using var dialog = new Form
        {
            Text = "Cactbot user 目录 / User directory", ClientSize = new Size(610, 178),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        var hint = new Label { Text = "输入或选择 user 目录（留空使用默认目录）。切换不会搬移已有文件。",
            AutoSize = true, Location = new Point(14, 16) };
        var input = new TextBox { Text = currentDirectory, Location = new Point(14, 46), Width = 482 };
        var browse = new Button { Text = "浏览…", Location = new Point(506, 44), Width = 88 };
        var error = new Label { ForeColor = Color.Firebrick, Location = new Point(14, 80), Size = new Size(580, 36) };
        var reset = new Button { Text = "使用默认", Location = new Point(14, 130), Width = 90 };
        var ok = new Button { Text = "确定", Location = new Point(406, 130), Width = 90 };
        var cancel = new Button { Text = "取消", Location = new Point(506, 130), Width = 88, DialogResult = DialogResult.Cancel };
        var result = currentDirectory;
        browse.Click += (_, _) =>
        {
            using var folder = new FolderBrowserDialog { Description = "Cactbot user 目录", UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(input.Text) ? input.Text : string.Empty };
            if (folder.ShowDialog(dialog) == DialogResult.OK) input.Text = folder.SelectedPath;
        };
        reset.Click += (_, _) => input.Text = string.Empty;
        ok.Click += (_, _) =>
        {
            try
            {
                result = Normalize(input.Text);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { error.Text = ex.Message; }
        };
        dialog.Controls.AddRange([hint, input, browse, error, reset, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        // Cactbot treats an empty reply as "use default", so cancellation must return
        // the previous path rather than silently discarding the user's configuration.
        return dialog.ShowDialog(owner) == DialogResult.OK ? result : currentDirectory;
    }

    internal static string Normalize(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) value = uri.LocalPath;
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("请输入完整的本地目录路径。 / Enter an absolute folder path.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在。 / The folder does not exist.");
        return path;
    }
}
