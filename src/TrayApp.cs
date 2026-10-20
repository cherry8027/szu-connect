using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.ComponentModel;

namespace SzuConnect
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Control dispatcher = new Control();
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly EventWaitHandle stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.StopName);
        private readonly EventWaitHandle settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SettingsName);
        private readonly RegisteredWaitHandle stopWait;
        private readonly RegisteredWaitHandle settingsWait;
        private readonly Portal portal = new Portal();
        private readonly ToolStripMenuItem statusItem = new ToolStripMenuItem("正在启动") { Enabled = false };
        private readonly ToolStripMenuItem nextItem = new ToolStripMenuItem("") { Enabled = false };
        private readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem("暂停监控");
        private Settings settings;
        private bool paused, busy, closing;
        private int failures;
        private string lastMessage = "";
        private Task runningCheck = Task.FromResult(0);
        private SettingsForm settingsDialog;

        internal TrayApp(bool showSettings)
        {
            var unused = dispatcher.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            settings = Settings.Load();
            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(nextItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("立即检查", null, delegate { QueueCheck(); });
            pauseItem.Click += delegate {
                paused = !paused;
                pauseItem.Text = paused ? "恢复监控" : "暂停监控";
                if (paused) { timer.Stop(); ShowStatus("已暂停", false, null); }
                else QueueCheck();
            };
            menu.Items.Add(pauseItem);
            menu.Items.Add("账号设置", null, delegate { OpenSettings(); });
            menu.Items.Add("查看日志", null, delegate { OpenLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { BeginExit(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "校园网助手", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenSettings(); };
            timer.Tick += delegate { timer.Stop(); runningCheck = CheckAsync(); };
            stopWait = ThreadPool.RegisterWaitForSingleObject(stopSignal, delegate {
                try { dispatcher.BeginInvoke(new Action(BeginExit)); }
                catch (InvalidOperationException) { }
            }, null, Timeout.Infinite, true);
            settingsWait = ThreadPool.RegisterWaitForSingleObject(settingsSignal, delegate {
                try { dispatcher.BeginInvoke(new Action(OpenSettings)); }
                catch (InvalidOperationException) { }
            }, null, Timeout.Infinite, false);
            Settings.Log("后台程序启动");
            if (showSettings || string.IsNullOrEmpty(settings.Username) || string.IsNullOrEmpty(settings.ProtectedPassword))
                dispatcher.BeginInvoke(new Action(OpenSettings));
            else QueueCheck();
        }

        private void QueueCheck()
        {
            if (busy || paused || closing) return;
            timer.Stop();
            timer.Interval = 100;
            timer.Start();
        }

        private async Task CheckAsync()
        {
            if (busy || paused || closing) return;
            busy = true;
            CheckResult result;
            try
            {
                ShowStatus("正在检测网络", false, null, false);
                result = await portal.CheckAsync(settings.Username, settings.Password(), shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                if (closing) { busy = false; return; }
                result = new CheckResult(false, "网络请求超时");
            }
            catch (Exception error)
            {
                Settings.Log("检测异常: " + error.GetType().Name);
                result = new CheckResult(false, "暂时无法连接认证服务器");
            }
            busy = false;
            if (closing || paused) return;
            failures = result.Online ? 0 : failures + 1;
            int delay = Portal.DelaySeconds(failures, settings.IntervalSeconds, result.CredentialError);
            ShowStatus(result.Message, result.Online, DateTime.Now.AddSeconds(delay));
            timer.Interval = delay * 1000;
            timer.Start();
        }

        private void ShowStatus(string message, bool online, DateTime? next, bool record = true)
        {
            statusItem.Text = message;
            nextItem.Text = next.HasValue ? "下次检测 " + next.Value.ToString("HH:mm:ss") : "";
            tray.Text = ("校园网助手 · " + message).Substring(0, Math.Min(63, ("校园网助手 · " + message).Length));
            tray.Icon = online ? SystemIcons.Information : SystemIcons.Application;
            if (!record) return;
            try { Settings.WriteStatus(message, online, next); }
            catch (Exception error) { Settings.Log("状态写入失败: " + error.GetType().Name); }
            if (lastMessage != message) { Settings.Log(message); lastMessage = message; }
        }

        private void OpenSettings()
        {
            if (closing) return;
            if (settingsDialog != null) { settingsDialog.Activate(); return; }
            timer.Stop();
            using (var dialog = new SettingsForm(settings))
            {
                settingsDialog = dialog;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    settings = dialog.Value;
                    failures = 0;
                    Settings.Log("账号设置已更新");
                }
            }
            settingsDialog = null;
            QueueCheck();
        }

        private void OpenLog()
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            if (!File.Exists(Settings.LogPath)) Settings.Log("日志已创建");
            Process.Start("notepad.exe", "\"" + Settings.LogPath + "\"");
        }

        private async void BeginExit()
        {
            if (closing) return;
            closing = true;
            if (settingsDialog != null) settingsDialog.Close();
            timer.Stop();
            shutdown.Cancel();
            tray.Visible = false;
            await runningCheck;
            ShowStatus("已停止", false, null);
            Settings.Log("后台程序退出");
            stopWait.Unregister(null);
            settingsWait.Unregister(null);
            stopSignal.Dispose();
            settingsSignal.Dispose();
            timer.Dispose();
            portal.Dispose();
            tray.Dispose();
            dispatcher.Dispose();
            shutdown.Dispose();
            ExitThread();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox username = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly NumericUpDown interval = new NumericUpDown { Minimum = 1, Maximum = 60, Increment = 1, Dock = DockStyle.Fill };
        private readonly CheckBox autostart = new CheckBox { Text = "登录 Windows 后自动启动", AutoSize = true };
        internal Settings Value { get; private set; }

        internal SettingsForm(Settings value)
        {
            Text = "校园网助手 · 账号设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(430, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            username.Text = value.Username;
            try { password.Text = value.Password(); } catch (System.Security.Cryptography.CryptographicException) { }
            catch (FormatException) { }
            interval.Value = Math.Ceiling(value.IntervalSeconds / 60m);
            autostart.Checked = Settings.Autostart;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 5 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            table.Controls.Add(new Label { Text = "校园网账号", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            table.Controls.Add(username, 1, 0);
            table.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            table.Controls.Add(password, 1, 1);
            table.Controls.Add(new Label { Text = "检测间隔（分钟）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            table.Controls.Add(interval, 1, 2);
            table.Controls.Add(autostart, 1, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "保存", AutoSize = true };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += delegate {
                if (string.IsNullOrWhiteSpace(username.Text) || string.IsNullOrEmpty(password.Text))
                { MessageBox.Show(this, "请输入账号和密码。", "校园网助手"); return; }
                Settings updated;
                try
                {
                    updated = new Settings { Username = username.Text.Trim(), IntervalSeconds = (int)interval.Value * 60 };
                    updated.SetPassword(password.Text);
                    updated.Save();
                }
                catch (Exception error) { ShowSaveError("保存账号设置失败", error); return; }
                Value = updated;
                try
                {
                    if (autostart.Checked != Settings.Autostart) Settings.SetAutostart(autostart.Checked);
                }
                catch (Exception error) { ShowSaveError("账号已保存，但开机启动设置失败", error); }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 0, 4);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void ShowSaveError(string message, Exception error)
        {
            var native = error as Win32Exception;
            string code = native != null ? native.NativeErrorCode.ToString() : "0x" + error.HResult.ToString("X8");
            Settings.Log(message + ": " + error.GetType().Name + " code=" + code);
            MessageBox.Show(this, message + "。\n错误代码：" + code + "\n详情请查看本地日志。", "校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
