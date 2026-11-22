using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BiliVideoDownloader
{
    internal sealed class MainWindow : Form
    {
        private static readonly Color Ink = Color.FromArgb(30, 35, 47);
        private static readonly Color Muted = Color.FromArgb(82, 91, 105);
        private static readonly Color Accent = Color.FromArgb(164, 42, 95);
        private static readonly Color AccentDark = Color.FromArgb(136, 32, 79);
        private static readonly Color Error = Color.FromArgb(151, 45, 45);
        private static readonly Color Success = Color.FromArgb(19, 103, 66);
        private static readonly Color Paper = Color.FromArgb(245, 246, 249);
        private static readonly Color Neutral = Color.FromArgb(235, 237, 242);

        private readonly DownloadEngine engine;
        private readonly TextBox urlBox = new TextBox();
        private readonly TextBox folderBox = new TextBox();
        private readonly ComboBox qualityBox = new ComboBox();
        private readonly Label videoTitle = TextLabel("尚未读取视频", 12, Ink, bold: true);
        private readonly Label videoDetails = TextLabel("读取后可选择当前可用的画质。", 9, Muted);
        private readonly Label status = TextLabel("等待视频链接", 10, Muted);
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button read = MakeButton("读取视频", true);
        private readonly Button browse = MakeButton("浏览…");
        private readonly Button download = MakeButton("下载 MP4", true);
        private readonly Button cancel = MakeButton("取消");
        private readonly Button open = MakeButton("打开文件夹");
        private readonly Button details = MakeButton("错误详情");

        private VideoInfo video;
        private CancellationTokenSource operation;
        private bool closeWhenDone;
        private string lastFile;
        private string lastError;

        public MainWindow(DownloadEngine engine)
        {
            this.engine = engine;
            Text = "B站视频下载器";
            if (File.Exists(Application.ExecutablePath))
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 561);
            MinimumSize = new Size(650, 600);
            BackColor = Paper;
            ForeColor = Ink;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Padding = new Padding(24, 19, 24, 19) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 216));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var heading = TextLabel("B站视频下载器", 23, Ink, bold: true);
            heading.Dock = DockStyle.None;
            heading.SetBounds(0, 0, 620, 42);
            var tagline = TextLabel("粘贴普通投稿链接，选择画质并保存视频。", 10, Muted);
            tagline.Dock = DockStyle.None;
            tagline.SetBounds(2, 45, 620, 22);
            header.Controls.AddRange(new Control[] { heading, tagline });
            layout.Controls.Add(header, 0, 0);

            var linkCard = Card(32, 38);
            var linkRow = FieldRow(urlBox, read);
            urlBox.AccessibleName = "视频链接";
            urlBox.MaxLength = 4096;
            linkCard.Controls.Add(TextLabel("视频链接", 10, Ink, bold: true), 0, 0);
            linkCard.Controls.Add(linkRow, 0, 1);
            layout.Controls.Add(linkCard, 0, 1);

            var outputCard = Card(33, 27, 31, 32, 27, 38);
            qualityBox.AccessibleName = "清晰度";
            qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
            qualityBox.Width = 242;
            qualityBox.Anchor = AnchorStyles.Left;
            folderBox.AccessibleName = "保存目录";
            folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            outputCard.Controls.Add(videoTitle, 0, 0);
            outputCard.Controls.Add(videoDetails, 0, 1);
            outputCard.Controls.Add(TextLabel("清晰度", 10, Ink, bold: true), 0, 2);
            outputCard.Controls.Add(qualityBox, 0, 3);
            outputCard.Controls.Add(TextLabel("保存目录", 10, Ink, bold: true), 0, 4);
            outputCard.Controls.Add(FieldRow(folderBox, browse), 0, 5);
            layout.Controls.Add(outputCard, 0, 2);

            var actionCard = Card(28, 13, 15, 38);
            actionCard.Margin = Padding.Empty;
            progress.AccessibleName = "下载进度";
            progress.Dock = DockStyle.Fill;
            progress.Margin = Padding.Empty;
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            actions.Controls.AddRange(new Control[] { download, cancel, open, details });
            foreach (Control button in actions.Controls)
                button.Margin = new Padding(0, 0, 8, 0);
            actionCard.Controls.Add(status, 0, 0);
            actionCard.Controls.Add(progress, 0, 1);
            actionCard.Controls.Add(actions, 0, 3);
            layout.Controls.Add(actionCard, 0, 3);

            urlBox.TextChanged += (sender, args) => { ResetVideo(); UpdateActions(); };
            urlBox.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter && read.Enabled)
                {
                    args.SuppressKeyPress = true;
                    read.PerformClick();
                }
            };
            qualityBox.SelectedIndexChanged += (sender, args) => UpdateActions();
            folderBox.TextChanged += (sender, args) => UpdateActions();
            read.Click += async (sender, args) => await ReadVideoAsync();
            download.Click += async (sender, args) => await SaveVideoAsync();
            cancel.Click += async (sender, args) => await CancelAsync();
            browse.Click += (sender, args) => BrowseFolder();
            open.Click += (sender, args) => ShowOutput();
            details.Click += (sender, args) => ShowError();
            FormClosing += ClosingWindow;
            Shown += (sender, args) =>
            {
                urlBox.Focus();
                string missing = engine.MissingComponent();
                if (missing != null)
                    SetStatus("缺少 " + missing + "。请完整解压软件文件夹。", Error);
            };
            UpdateActions();
        }

        private void ResetVideo()
        {
            video = null;
            lastFile = null;
            lastError = null;
            qualityBox.Items.Clear();
            videoTitle.Text = "尚未读取视频";
            videoDetails.Text = "读取后可选择当前可用的画质。";
            SetStatus(String.IsNullOrWhiteSpace(urlBox.Text) ? "等待视频链接" : "点击「读取视频」查看可用画质", Muted, 0);
        }

        private async Task ReadVideoAsync()
        {
            ResetVideo();
            await RunAsync("正在读取视频信息…", async token =>
            {
                var report = new Progress<string>(message =>
                {
                    if (operation != null && !token.IsCancellationRequested)
                        SetStatus(message, Muted);
                });
                video = await engine.InspectAsync(urlBox.Text, token, report);
                videoTitle.Text = String.IsNullOrWhiteSpace(video.Title) ? "B站视频" : video.Title;
                string[] fields = { video.Uploader, video.Duration };
                videoDetails.Text = String.Join("   ·   ", Array.FindAll(fields, field => !String.IsNullOrWhiteSpace(field)));
                qualityBox.Items.AddRange(video.Qualities.ToArray());
                qualityBox.SelectedIndex = 0;
                SetStatus("可以下载", Muted, 0);
            });
        }

        private async Task SaveVideoAsync()
        {
            var quality = (VideoQuality)qualityBox.SelectedItem;
            lastFile = null;
            await RunAsync("正在连接下载引擎…", async token =>
            {
                string value = folderBox.Text.Trim();
                bool drivePath = value.Length >= 3 && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
                if (!drivePath && !value.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new ArgumentException("请选择完整的保存目录，例如 D:\\视频。");

                string folder = Path.GetFullPath(value);
                var report = new Progress<DownloadProgress>(update =>
                {
                    if (operation != null && !token.IsCancellationRequested)
                        SetStatus(update.Message, Muted, update.Percent);
                });
                lastFile = await engine.DownloadAsync(video, quality, folder, report, token);
                SetStatus("下载完成 · " + Path.GetFileName(lastFile), Success, 100);
            });
        }

        private async Task RunAsync(string initialStatus, Func<CancellationToken, Task> work)
        {
            lastError = null;
            operation = new CancellationTokenSource();
            SetStatus(initialStatus, Muted);
            UpdateActions();
            try
            {
                await work(operation.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus(operation.IsCancellationRequested ? "已取消" : "连接超时，请重试。", Muted, 0);
            }
            catch (DownloadException error)
            {
                lastError = error.Details;
                SetStatus(error.Message, Error, 0);
            }
            catch (Exception error)
            {
                lastError = error.Message;
                SetStatus(error is ArgumentException || error is InvalidOperationException
                    ? error.Message : "操作失败，请查看错误详情。", Error, 0);
            }
            finally
            {
                operation.Dispose();
                operation = null;
                UpdateActions();
                if (closeWhenDone)
                    Close();
            }
        }

        private async Task CancelAsync()
        {
            if (operation == null || !cancel.Enabled)
                return;

            CancellationTokenSource current = operation;
            cancel.Enabled = false;
            SetStatus("正在取消…", Muted);
            // Process cancellation can wait briefly for the child process tree to exit.
            await Task.Run(() =>
            {
                try { current.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }

        private async void ClosingWindow(object sender, FormClosingEventArgs args)
        {
            if (operation == null)
                return;

            args.Cancel = true;
            closeWhenDone = true;
            await CancelAsync();
        }

        private void UpdateActions()
        {
            bool idle = operation == null;
            urlBox.Enabled = idle;
            read.Enabled = idle && !String.IsNullOrWhiteSpace(urlBox.Text);
            folderBox.Enabled = idle;
            qualityBox.Enabled = idle && video != null;
            browse.Enabled = idle;
            download.Enabled = idle && video != null && qualityBox.SelectedItem != null &&
                !String.IsNullOrWhiteSpace(folderBox.Text);
            cancel.Visible = !idle;
            cancel.Enabled = !idle;
            open.Visible = idle && lastFile != null;
            details.Visible = idle && !String.IsNullOrEmpty(lastError);
        }

        private void SetStatus(string message, Color color, int? percent = null)
        {
            status.Text = message;
            status.ForeColor = color;
            progress.Style = percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = percent.HasValue ? 0 : 25;
            if (percent.HasValue)
                progress.Value = percent.Value;
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择视频保存目录",
                SelectedPath = Directory.Exists(folderBox.Text) ? folderBox.Text : "" })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    folderBox.Text = dialog.SelectedPath;
            }
        }

        private void ShowOutput()
        {
            if (lastFile == null)
                return;

            string directory = Path.GetDirectoryName(lastFile);
            if (!Directory.Exists(directory))
            {
                SetStatus("保存目录已移动或不可访问。", Error, 0);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                lastError = error.Message;
                SetStatus("无法打开保存目录。", Error, 0);
                UpdateActions();
            }
        }

        private void ShowError()
        {
            using (var dialog = new Form { Text = "错误详情", StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(620, 305), MinimumSize = new Size(435, 230), Font = Font,
                ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, BackColor = Color.White })
            {
                var body = new TextBox { Text = lastError.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48,
                    Padding = new Padding(8, 7, 8, 0), FlowDirection = FlowDirection.RightToLeft };
                var close = MakeButton("关闭");
                close.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(close);
                dialog.Controls.Add(body);
                dialog.Controls.Add(buttons);
                dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }

        private static TableLayoutPanel Card(params int[] rowHeights)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White,
                Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 8, 16, 8), ColumnCount = 1,
                RowCount = rowHeights.Length };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var height in rowHeights)
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            return card;
        }

        private static TableLayoutPanel FieldRow(TextBox field, Button button)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            field.Margin = new Padding(0, 0, 10, 0);
            button.Anchor = AnchorStyles.Right;
            row.Controls.Add(field, 0, 0);
            row.Controls.Add(button, 1, 0);
            return row;
        }

        private static Label TextLabel(string text, int size, Color color, bool bold = false)
        {
            return new Label { Text = text, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color, Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }

        private static Button MakeButton(string text, bool primary = false)
        {
            var button = new Button { Text = text, Width = 105, Height = 34, FlatStyle = FlatStyle.Flat,
                Margin = Padding.Empty, BackColor = primary ? Accent : Neutral, ForeColor = primary ? Color.White : Ink,
                UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentDark : Color.FromArgb(223, 227, 234);
            return button;
        }
    }
}
