using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SzuConnect
{
    internal sealed class LogForm : Form
    {
        private readonly TextBox content = new TextBox {
            Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F), BackColor = SystemColors.Window
        };
        private readonly Label status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 0, 0) };

        internal LogForm()
        {
            Text = "校园网助手 · 日志";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(800, 480);
            MinimumSize = new Size(480, 300);
            StartPosition = FormStartPosition.CenterScreen;
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            var refresh = new Button { Text = "刷新", AutoSize = true };
            refresh.Click += delegate { Reload(); };
            toolbar.Controls.Add(refresh);
            toolbar.Controls.Add(status);
            Controls.Add(content);
            Controls.Add(toolbar);
        }

        internal static string ReadLog(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
        }

        internal void Reload()
        {
            try
            {
                content.Text = ReadLog(Settings.LogPath);
                content.SelectionStart = content.TextLength;
                content.ScrollToCaret();
                status.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (FileNotFoundException) { content.Clear(); status.Text = "暂无日志"; }
            catch (DirectoryNotFoundException) { content.Clear(); status.Text = "暂无日志"; }
            catch (Exception error)
            {
                Settings.Log("查看日志失败: " + error.GetType().Name);
                status.Text = "读取日志失败（" + error.GetType().Name + "）";
            }
        }
    }
}
