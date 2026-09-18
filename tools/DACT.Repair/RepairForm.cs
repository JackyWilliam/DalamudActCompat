using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("DACT 修复工具")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DactRepair
{
    internal sealed class RepairForm : Form
    {
        private readonly ComboBox installations = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button browse = new Button { Text = "选择目录…", Dock = DockStyle.Fill };
        private readonly Button repair = new Button { Text = "一键修复到 0.4.4.0", Dock = DockStyle.Fill, Height = 48 };
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
        private bool busy;

        internal RepairForm(bool discover)
        {
            Text = "DACT 修复工具 1.0";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(740, 470);
            MinimumSize = new Size(680, 490);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(242, 245, 248);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = "DACT 修复工具", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(24, 54, 78) });
            layout.Controls.Add(new Label { Text = "修复错误编号 4.4.0.0，安装正式版 0.4.4.0。\r\n请先关闭游戏和启动器，再点击下方按钮。", Dock = DockStyle.Fill });
            layout.Controls.Add(new Label { Text = "启动器数据目录", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 85, 100) });
            var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            selection.Controls.Add(installations); selection.Controls.Add(browse);
            layout.Controls.Add(selection);
            layout.Controls.Add(log);
            repair.FlatStyle = FlatStyle.Flat;
            repair.BackColor = Color.FromArgb(29, 103, 149); repair.ForeColor = Color.White;
            repair.Margin = new Padding(0, 10, 0, 4);
            layout.Controls.Add(repair);
            layout.Controls.Add(new Label { Text = "保留配置、账号和触发器 · 自动备份旧版 · 失败自动恢复", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(85, 101, 114) });
            Controls.Add(layout);
            if (discover)
            {
                foreach (var path in RepairEngine.Discover()) installations.Items.Add(path);
                if (installations.Items.Count == 1) installations.SelectedIndex = 0;
            }
            log.Text = "工具内置已校验的官方安装包，修复过程无需下载。\r\n只处理 4.4.0.0 版本号错误，不会重置插件配置。\r\n未自动找到目录时，请选择包含 installedPlugins 的启动器数据目录。";
            if (installations.Items.Count > 1) log.AppendText("\r\n检测到多个安装，请先选择你使用的启动器目录。");
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择包含 installedPlugins 的启动器数据目录", ShowNewFolderButton = false })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var path = Path.GetFullPath(dialog.SelectedPath);
                    if (!installations.Items.Contains(path)) installations.Items.Add(path);
                    installations.SelectedItem = path;
                }
            };
            repair.Click += async delegate { await RunRepair(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }

        private async Task RunRepair()
        {
            if (installations.SelectedItem == null) { Append("请先选择启动器数据目录。"); return; }
            var root = installations.SelectedItem.ToString();
            busy = true; repair.Enabled = false; browse.Enabled = false; installations.Enabled = false;
            repair.Text = "正在修复，请稍候…";
            try
            {
                var result = await Task.Run(() =>
                {
                    using (var package = RepairEngine.OpenPackage())
                        return RepairEngine.Repair(root, package, message => Invoke(new Action(() => Append(message))), RepairEngine.RunningProcess);
                });
                Append(result.Message);
            }
            catch (Exception error) { Append(error.Message); }
            finally
            {
                busy = false; repair.Enabled = true; browse.Enabled = true; installations.Enabled = true;
                repair.Text = "一键修复到 0.4.4.0";
            }
        }

        private void Append(string message) { log.AppendText(Environment.NewLine + message); }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\DACT-Version-Repair", out created))
            {
                if (!created) { MessageBox.Show("DACT 修复工具已经打开。", "DACT 修复工具"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RepairForm(true));
            }
        }
    }
}
