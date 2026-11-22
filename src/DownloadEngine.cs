using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BiliVideoDownloader
{
    internal sealed class VideoInfo
    {
        public string Url;
        public string Title;
        public string Uploader;
        public string Duration;
        public List<VideoQuality> Qualities;
        public string PlayerMetadata;
    }

    internal sealed class VideoQuality
    {
        public string VideoFormat;
        public string AudioFormat;
        public string Label;

        public override string ToString()
        {
            return Label;
        }
    }

    internal sealed class DownloadProgress
    {
        public readonly string Message;
        public readonly int? Percent;

        public DownloadProgress(string message, int? percent = null)
        {
            Message = message;
            Percent = percent;
        }
    }

    internal sealed class DownloadException : Exception
    {
        public readonly string Details;

        public DownloadException(string message, string details)
            : base(message)
        {
            Details = details;
        }
    }

    internal sealed class DownloadEngine
    {
        private const string ProgressPrefix = "BILI_PROGRESS:";
        private const string MergePrefix = "BILI_MERGE:";
        private const string FilePrefix = "BILI_FILE:";
        private static readonly Regex VideoPath = new Regex(
            @"^/(?:s/)?video/(BV[a-z0-9]{10}|av[0-9]+)/?$", RegexOptions.IgnoreCase);
        private static readonly Regex FormatId = new Regex(@"^[a-zA-Z0-9._-]+$");
        private static readonly Dictionary<int, string> QualityTiers = new Dictionary<int, string> {
            { 6, "240P" }, { 16, "360P" }, { 32, "480P" }, { 64, "720P" }, { 74, "720P60" },
            { 80, "1080P" }, { 112, "1080P 高码率" }, { 116, "1080P60" }, { 120, "4K" },
            { 125, "HDR" }, { 126, "杜比视界" }, { 127, "8K" }
        };

        private readonly string toolsDirectory;
        private readonly string extractor;

        public DownloadEngine(string appDirectory)
        {
            toolsDirectory = Path.Combine(appDirectory, "tools");
            extractor = Path.Combine(toolsDirectory, "yt-dlp.exe");
        }

        internal string MissingComponent()
        {
            return new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe" }
                .FirstOrDefault(file => !File.Exists(Path.Combine(toolsDirectory, file)));
        }

        public async Task<VideoInfo> InspectAsync(string address, CancellationToken token,
            IProgress<string> status = null)
        {
            Uri input = ReadAddress(address);
            string url = await ResolveAsync(input, token).ConfigureAwait(false);
            DownloadException firstError;
            try
            {
                return await Task.Run(() =>
                {
                    var output = new StringBuilder();
                    Run(new[] { "--dump-single-json", url }, line => output.AppendLine(line), token);
                    return ParseVideo(url, output.ToString());
                }, token).ConfigureAwait(false);
            }
            catch (DownloadException error)
            {
                if (!error.Details.Contains("HTTP Error 412"))
                    throw;
                firstError = error;
            }

            if (status != null)
                status.Report("正在通过 B 站播放器读取视频…");
            try
            {
                return await BilibiliPlayer.InspectAsync(new Uri(url), token).ConfigureAwait(false);
            }
            catch (Exception playerError)
            {
                token.ThrowIfCancellationRequested();
                throw new DownloadException("普通网页和播放器入口均无法读取此视频。请查看错误详情。",
                    firstError.Details + Environment.NewLine + Environment.NewLine + "播放器入口：" + playerError.Message);
            }
        }

        public Task<string> DownloadAsync(VideoInfo video, VideoQuality quality, string folder,
            IProgress<DownloadProgress> progress, CancellationToken token)
        {
            return Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                string file = null;
                string selection = quality.AudioFormat == null
                    ? quality.VideoFormat : quality.VideoFormat + "+" + quality.AudioFormat;
                Action reportMerge = () => progress.Report(new DownloadProgress("正在合并或整理 MP4…"));

                var arguments = new List<string> {
                    "--no-simulate", "--no-overwrites", "--progress", "--newline",
                    "--progress-delta", "0.4", "--windows-filenames", "--paths", folder,
                    "--output", "%(title).120B [%(id)s] [%(format_id)s].%(ext)s",
                    "--format", selection, "--merge-output-format", "mp4",
                    "--ffmpeg-location", toolsDirectory,
                    "--progress-template", "download:" + ProgressPrefix +
                        "%(progress._percent_str)s|%(progress._speed_str)s|%(info.format_id)s",
                    "--progress-template", "postprocess:" + MergePrefix + "%(progress.status)s",
                    "--print", "after_move:" + FilePrefix + "%(filepath)j"
                };
                string metadataPath = video.PlayerMetadata == null ? null :
                    Path.Combine(Path.GetTempPath(), "BiliPlayer-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    if (metadataPath == null)
                        arguments.Add(video.Url);
                    else
                    {
                        File.WriteAllText(metadataPath, video.PlayerMetadata, new UTF8Encoding(false));
                        // Some Bilibili CDN streams require an explicit end offset even for the first request.
                        arguments.AddRange(new[] { "--http-chunk-size", "8M", "--load-info-json", metadataPath });
                    }

                    Action<string> readOutput = line =>
                    {
                        if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
                            file = Json().Deserialize<string>(line.Substring(FilePrefix.Length));
                        else if (line.StartsWith(MergePrefix, StringComparison.Ordinal))
                            reportMerge();
                        else if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                            ReportDownload(line.Substring(ProgressPrefix.Length), quality, progress);
                    };
                    try
                    {
                        Run(arguments, readOutput, token, reportMerge);
                    }
                    catch (DownloadException error)
                    {
                        if (metadataPath == null || (!error.Details.Contains("HTTP Error 403") &&
                            !error.Details.Contains("HTTP Error 567")))
                            throw;

                        string backup = BilibiliPlayer.BackupMetadata(video.PlayerMetadata, quality);
                        if (backup == null)
                            throw;

                        progress.Report(new DownloadProgress("正在尝试 B 站备用媒体地址…"));
                        File.WriteAllText(metadataPath, backup, new UTF8Encoding(false));
                        Run(arguments, readOutput, token, reportMerge);
                    }
                }
                finally
                {
                    if (metadataPath != null)
                        File.Delete(metadataPath);
                }

                if (String.IsNullOrEmpty(file) || !File.Exists(file) ||
                    !String.Equals(Path.GetExtension(file), ".mp4", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("下载引擎已退出，但未确认生成 MP4 文件。");

                return Path.GetFullPath(file);
            }, token);
        }

        internal static Uri ReadAddress(string address)
        {
            if (!Regex.IsMatch(address.Trim(), @"^https?://", RegexOptions.IgnoreCase))
                address = "https://" + address.Trim();

            Uri url;
            if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out url) ||
                !url.IsDefaultPort || url.UserInfo.Length > 0 ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("请粘贴一个完整的 B 站视频链接。");

            if (url.Host == "b23.tv" && Regex.IsMatch(url.AbsolutePath, @"^/[a-zA-Z0-9]+/?$"))
                return new Uri("https://b23.tv" + url.AbsolutePath);

            return new Uri(NormalizeVideo(url));
        }

        private static string NormalizeVideo(Uri url)
        {
            Match path = VideoPath.Match(url.AbsolutePath);
            if ((url.Host != "www.bilibili.com" && url.Host != "bilibili.com" &&
                url.Host != "m.bilibili.com") || !path.Success)
                throw new ArgumentException("请输入 B 站普通投稿的 BV、AV 链接，或使用 b23.tv 分享链接。");

            int part;
            Match page = Regex.Match(url.Query, @"[?&]p=([0-9]+)(?:&|$)");
            string query = page.Success && Int32.TryParse(page.Groups[1].Value, out part) && part > 0
                ? "?p=" + part.ToString(CultureInfo.InvariantCulture) : "";
            string id = path.Groups[1].Value;
            id = (id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? "av" : "BV") + id.Substring(2);
            return "https://www.bilibili.com/video/" + id + "/" + query;
        }

        private static async Task<string> ResolveAsync(Uri url, CancellationToken token)
        {
            if (url.Host != "b23.tv")
                return url.AbsoluteUri;

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BiliVideoDownloader/1.0");
                for (int redirects = 0; redirects < 4; redirects++)
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status < 300 || status >= 400 || response.Headers.Location == null)
                        break;

                    url = ReadAddress(new Uri(url, response.Headers.Location).AbsoluteUri);
                    if (url.Host != "b23.tv")
                        return url.AbsoluteUri;
                }
            }
            throw new InvalidOperationException("分享短链未跳转到视频。请复制 Chrome 地址栏里的完整视频链接。");
        }

        internal static VideoInfo ParseVideo(string url, string json)
        {
            var data = Json().Deserialize<Dictionary<string, object>>(json);
            object entries;
            if (data == null || !data.TryGetValue("formats", out entries) || !(entries is IEnumerable))
                throw new InvalidOperationException("没有读到单条视频的画质信息。");

            var formats = ((IEnumerable)entries).OfType<Dictionary<string, object>>()
                .Where(entry => FormatId.IsMatch(Value(entry, "format_id")) &&
                    !String.Equals(Value(entry, "has_drm"), "True", StringComparison.OrdinalIgnoreCase))
                .Select(entry => new MediaFormat(entry)).ToList();
            MediaFormat audio = formats.Where(f => !f.HasVideo && f.HasAudio && f.Extension == "m4a")
                .OrderByDescending(f => f.Bitrate).FirstOrDefault();

            var qualities = formats.Where(f => f.HasVideo && f.Extension == "mp4" &&
                    f.ShortEdge > 0 && (f.HasAudio || audio != null))
                .GroupBy(f => new { f.QualityId, f.ShortEdge, HighFrameRate = f.FrameRate >= 50 })
                .Select(group => group.OrderByDescending(f => f.H264)
                    .ThenByDescending(f => f.Bitrate).First())
                .OrderByDescending(f => f.ShortEdge).ThenByDescending(f => f.FrameRate)
                .Select(f => new VideoQuality {
                    VideoFormat = f.Id,
                    AudioFormat = f.HasAudio ? null : audio.Id,
                    Label = f.ResolutionLabel +
                        (f.FrameRate >= 50 && f.QualityId != 74 && f.QualityId != 116
                            ? " · " + Math.Round(f.FrameRate) + " 帧" : "") +
                        (f.H264 || f.CodecLabel == "" ? "" : " · " + f.CodecLabel)
                }).ToList();

            if (qualities.Count == 0)
                throw new InvalidOperationException("当前没有可下载并包含音频的 MP4 画质。");

            return new VideoInfo {
                Url = url, Title = Value(data, "title"), Uploader = Value(data, "uploader"),
                Duration = Value(data, "duration_string"), Qualities = qualities
            };
        }

        private sealed class MediaFormat
        {
            public readonly string Id, Extension, CodecLabel, ResolutionLabel;
            public readonly bool HasAudio, HasVideo, H264;
            public readonly int ShortEdge, QualityId;
            public readonly double FrameRate, Bitrate;

            public MediaFormat(Dictionary<string, object> entry)
            {
                Id = Value(entry, "format_id");
                Extension = Value(entry, "ext");
                string video = Value(entry, "vcodec");
                string audio = Value(entry, "acodec");
                HasVideo = video.Length > 0 && video != "none";
                HasAudio = audio.Length > 0 && audio != "none";
                H264 = video.StartsWith("avc", StringComparison.Ordinal) || video == "h264";
                CodecLabel = video.StartsWith("av01", StringComparison.Ordinal) ? "AV1" :
                    (video.StartsWith("hev", StringComparison.Ordinal) || video.StartsWith("hvc", StringComparison.Ordinal)
                        ? "HEVC" : "");
                int height = (int)Number(entry, "height");
                int width = (int)Number(entry, "width");
                ShortEdge = width > 0 && height > 0 ? Math.Min(width, height) : Math.Max(width, height);
                QualityId = (int)Number(entry, "quality");
                string tier;
                string dimensions = width > 0 && height > 0 ? width + "×" + height : "";
                if (QualityTiers.TryGetValue(QualityId, out tier))
                    ResolutionLabel = dimensions.Length > 0 ? tier + "（" + dimensions + "）" : tier;
                else
                    ResolutionLabel = dimensions.Length > 0 ? dimensions : ShortEdge + " 像素";
                FrameRate = Number(entry, "fps");
                Bitrate = Number(entry, HasVideo ? "tbr" : "abr");
                if (Bitrate == 0)
                    Bitrate = Number(entry, HasVideo ? "vbr" : "tbr");
            }
        }

        internal static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        }

        internal static string Value(Dictionary<string, object> entry, string key)
        {
            object value;
            return entry.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        internal static double Number(Dictionary<string, object> entry, string key)
        {
            double value;
            return Double.TryParse(Value(entry, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        private static void ReportDownload(string payload, VideoQuality quality, IProgress<DownloadProgress> progress)
        {
            string[] values = payload.Split('|');
            if (values.Length != 3)
                return;

            string label = quality.AudioFormat != null && values[2] == quality.AudioFormat
                ? "正在下载音频" : "正在下载视频";
            double percent;
            int? position = Double.TryParse(values[0].Trim().TrimEnd('%'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out percent) ? (int?)Math.Max(0, Math.Min(100, percent)) : null;
            string speed = values[1].Trim();
            if (position.HasValue)
                label += "   " + position.Value + "%";
            if (speed.Length > 0 && speed != "NA" && speed != "Unknown")
                label += "   ·   " + speed;
            progress.Report(new DownloadProgress(label, position));
        }

        private void Run(IEnumerable<string> arguments, Action<string> readOutput, CancellationToken token,
            Action reportPostProcess = null)
        {
            token.ThrowIfCancellationRequested();
            string missing = MissingComponent();
            if (missing != null)
                throw new InvalidOperationException("缺少组件 " + missing + "。请完整解压软件后再运行。");

            var errors = new StringBuilder();
            Exception outputError = null;
            var defaults = new[] { "--ignore-config", "--no-plugin-dirs", "--encoding", "utf-8",
                "--color", "never", "--socket-timeout", "20", "--retries", "3", "--no-playlist" };

            using (var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = extractor, WorkingDirectory = toolsDirectory,
                Arguments = String.Join(" ", defaults.Concat(arguments).Select(QuoteArgument)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            } })
            {
                process.OutputDataReceived += (sender, eventArgs) =>
                {
                    if (eventArgs.Data == null || outputError != null)
                        return;
                    try { readOutput(eventArgs.Data); }
                    catch (Exception error) { outputError = error; }
                };
                process.ErrorDataReceived += (sender, eventArgs) =>
                {
                    if (eventArgs.Data == null)
                        return;
                    if (reportPostProcess != null && eventArgs.Data.StartsWith(MergePrefix, StringComparison.Ordinal))
                        reportPostProcess();
                    else if (errors.Length < 16000)
                        errors.AppendLine(eventArgs.Data);
                };

                process.Start();
                using (token.Register(() => StopProcess(process)))
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                }

                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    string detail = errors.ToString().Trim();
                    string message = detail.Contains("HTTP Error 412") || detail.Contains("HTTP Error 403")
                        ? "B 站暂时拒绝了请求。可以稍后重试，或换一条公开视频。"
                        : "无法完成此次操作。请检查视频是否能公开播放，或查看错误详情。";
                    throw new DownloadException(message, detail.Length == 0 ? "yt-dlp exit code " + process.ExitCode : detail);
                }
                if (outputError != null)
                    throw new DownloadException("下载引擎返回的信息无法读取。", outputError.Message);
            }
        }

        private static void StopProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                using (var stop = Process.Start(new ProcessStartInfo {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = "/PID " + process.Id + " /T /F", UseShellExecute = false, CreateNoWindow = true
                }))
                    stop.WaitForExit(2000);
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        internal static string QuoteArgument(string argument)
        {
            // Windows treats backslashes before a quote or the closing delimiter specially.
            string escaped = Regex.Replace(argument, "(\\\\*)\"", "$1$1\\\"");
            escaped = Regex.Replace(escaped, "(\\\\+)$", "$1$1");
            return "\"" + escaped + "\"";
        }
    }
}
