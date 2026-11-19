using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class SetupText
{
    internal static string Get(string chinese, string english)
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? chinese : english;
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox directory = new TextBox { Dock = DockStyle.Fill };
    private readonly Button browse = new Button { AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Button install = new Button { AutoSize = true };
    private readonly Button cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label description = new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 14) };
    private readonly string version;
    private readonly string previous;
    private readonly bool hasOtherInstallations;
    private bool busy;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var mutex = new Mutex(false, @"Local\CurtainPrivacy.Setup"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                MessageBox.Show(SetupText.Get("Curtain 安装程序已打开。", "Curtain Setup is already open."), "Curtain");
                return;
            }
            try
            {
                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                Application.Run(new SetupForm(info.InformationalVersion));
            }
            catch (Exception error) { ShowError(null, error); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private SetupForm(string version)
    {
        this.version = version;
        string[] installations = SetupInstallation.KnownRoots();
        previous = installations.FirstOrDefault();
        hasOtherInstallations = installations.Length > 1;
        Text = SetupText.Get("Curtain 安装", "Curtain Setup");
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(610, 224);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "Curtain " + version, AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(new Label { AutoSize = true, Text = SetupText.Get("安装目录", "Installation folder"), Margin = new Padding(0, 0, 0, 4) }, 0, 1);
        layout.Controls.Add(directory, 0, 2);
        browse.Text = SetupText.Get("浏览…", "Browse…");
        browse.Click += Browse;
        layout.Controls.Add(browse, 1, 2);
        description.MaximumSize = new Size(550, 0);
        layout.Controls.Add(description, 0, 3);
        layout.SetColumnSpan(description, 2);
        layout.Controls.Add(progress, 0, 4);
        layout.SetColumnSpan(progress, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        cancel.Text = SetupText.Get("取消", "Cancel");
        actions.Controls.Add(cancel);
        actions.Controls.Add(install);
        layout.Controls.Add(actions, 0, 5);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = cancel;
        install.Click += Install;
        directory.TextChanged += (_, __) => UpdateMode();
        directory.Text = previous ?? InstallationPaths.DefaultRoot;
        UpdateMode();
    }

    private void UpdateMode()
    {
        bool move = previous != null && !InstallationPaths.Equal(previous, directory.Text.Trim().TrimEnd('\\'));
        install.Text = move ? SetupText.Get("迁移并安装", "Move and install") :
            hasOtherInstallations ? SetupText.Get("修复安装", "Repair installation") : SetupText.Get("安装", "Install");
        description.Text = move || hasOtherInstallations
            ? SetupText.Get("安装完成后会清理其他位置的旧程序，原有设置会保留。",
                "Setup removes old program files from other locations after installation. Your settings are kept.")
            : previous != null
                ? SetupText.Get("安装到当前目录并保留设置。", "Install in the current folder and keep your settings.")
                : SetupText.Get("可以选择其他本机磁盘上的文件夹。", "You can choose a folder on another local drive.");
    }

    private void Browse(object sender, EventArgs args)
    {
        using (var picker = new FolderBrowserDialog())
        {
            picker.Description = SetupText.Get("选择安装文件夹", "Choose an installation folder");
            if (Directory.Exists(directory.Text)) picker.SelectedPath = directory.Text;
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string selected = picker.SelectedPath;
                if (!InstallationPaths.Equal(selected, previous) &&
                    (InstallationPaths.Equal(Path.GetPathRoot(selected), selected) || Directory.EnumerateFileSystemEntries(selected).Any()) &&
                    !InstallationPaths.Equal(Path.GetFileName(selected), InstallationPaths.PackageId))
                    selected = Path.Combine(selected, InstallationPaths.PackageId);
                directory.Text = selected;
            }
            catch (Exception error) { ShowError(this, error); }
        }
    }

    private async void Install(object sender, EventArgs args)
    {
        string requested = directory.Text;
        busy = true;
        directory.Enabled = browse.Enabled = install.Enabled = cancel.Enabled = false;
        progress.Visible = true;
        description.Text = SetupText.Get("正在安装 Curtain…", "Installing Curtain…");
        Tuple<string, string> result;
        try
        {
            result = await Task.Run(() =>
            {
                string notice;
                string launcher = SetupInstallation.Install(requested, version, out notice);
                return Tuple.Create(launcher, notice);
            });
        }
        catch (Exception error)
        {
            ShowError(this, error);
            return;
        }
        finally
        {
            busy = false;
            progress.Visible = false;
            directory.Enabled = browse.Enabled = install.Enabled = cancel.Enabled = true;
            UpdateMode();
        }
        if (result.Item2 != null) MessageBox.Show(this, result.Item2, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { Process.Start(new ProcessStartInfo(result.Item1) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(result.Item1) }); }
        catch (Win32Exception)
        {
            MessageBox.Show(this, SetupText.Get("安装完成。请从开始菜单打开 Curtain。", "Installation is complete. Open Curtain from the Start menu."), Text);
        }
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private static void ShowError(IWin32Window owner, Exception error)
    {
        string message = error is InstallProblem ? error.Message : SetupText.Get(
            "安装未完成。请确认所选目录可写，并检查 Curtain 是否仍在运行。",
            "Installation did not complete. Check that the folder is writable and that Curtain is closed.");
        MessageBox.Show(owner, message, SetupText.Get("Curtain 安装", "Curtain Setup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
