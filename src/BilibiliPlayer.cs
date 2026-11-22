using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JsonMap = System.Collections.Generic.Dictionary<string, object>;

namespace BiliVideoDownloader
{
    internal static class BilibiliPlayer
    {
        private const string Api = "https://api.bilibili.com";
        private const string PlayerOrigin = "https://player.bilibili.com";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/160.0.0.0 Safari/537.36";

        public static async Task<VideoInfo> InspectAsync(Uri url, CancellationToken token)
        {
            string id = url.AbsolutePath.TrimEnd('/').Split('/').Last();
            int partNumber = url.Query.Length == 0 ? 1 : Int32.Parse(url.Query.Substring(3), CultureInfo.InvariantCulture);
            string identifier = id.StartsWith("av", StringComparison.Ordinal) ? "aid=" + id.Substring(2) : "bvid=" + id;
            var player = new Uri(PlayerOrigin + "/player.html?isOutside=true&" + identifier);

            using (var handler = new HttpClientHandler {
                AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                // Start a guest session in the official embedded player before using its APIs.
                using (var response = await client.GetAsync(player, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false))
                    response.EnsureSuccessStatusCode();

                client.DefaultRequestHeaders.Referrer = player;
                client.DefaultRequestHeaders.Add("Origin", PlayerOrigin);
                object pages = await GetDataAsync(client, "/x/player/pagelist?" + identifier, token).ConfigureAwait(false);
                JsonMap part = Maps(pages).FirstOrDefault(page => DownloadEngine.Number(page, "page") == partNumber);
                if (part == null)
                    throw new InvalidOperationException("播放器找不到指定的分 P。");

                long cid = (long)DownloadEngine.Number(part, "cid");
                if (cid <= 0)
                    throw new InvalidOperationException("播放器没有返回分 P 编号。");

                string playPath = "/x/player/playurl?" + identifier + "&cid=" + cid.ToString(CultureInfo.InvariantCulture) +
                    "&qn=64&fnver=0&fnval=4048";
                var playback = await GetDataAsync(client, playPath, token).ConfigureAwait(false) as JsonMap;
                return ParsePlayback(url, player, id, partNumber, part, playback);
            }
        }

        private static async Task<object> GetDataAsync(HttpClient client, string path, CancellationToken token)
        {
            using (var response = await client.GetAsync(Api + path, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var data = DownloadEngine.Json().Deserialize<JsonMap>(content);
                string code = data == null ? "" : DownloadEngine.Value(data, "code");
                if (code != "0")
                    throw new InvalidOperationException("B 站播放器返回错误 " + code + "。");
                return Entry(data, "data");
            }
        }

        internal static VideoInfo ParsePlayback(Uri url, Uri player, string id, int partNumber, JsonMap part, JsonMap playback)
        {
            var dash = Entry(playback, "dash") as JsonMap;
            var formats = ReadStreams(Maps(Entry(dash, "video")), true)
                .Concat(ReadStreams(Maps(Entry(dash, "audio")), false)).ToList();
            string title = DownloadEngine.Value(part, "part");
            double duration = DownloadEngine.Number(part, "duration");
            var metadata = new JsonMap {
                { "id", partNumber == 1 ? id : id + "_p" + partNumber },
                { "title", String.IsNullOrWhiteSpace(title) ? id : title },
                { "webpage_url", url.AbsoluteUri },
                { "duration", duration },
                { "duration_string", duration > 0 ? TimeSpan.FromSeconds(duration).ToString(@"h\:mm\:ss") : "" },
                { "formats", formats },
                { "http_headers", new JsonMap {
                    { "Referer", player.AbsoluteUri }, { "User-Agent", UserAgent }, { "Range", "bytes=0-" }
                } }
            };

            string json = DownloadEngine.Json().Serialize(metadata);
            VideoInfo video = DownloadEngine.ParseVideo(url.AbsoluteUri, json);
            video.PlayerMetadata = json;
            return video;
        }

        private static IEnumerable<JsonMap> ReadStreams(IEnumerable<JsonMap> streams, bool isVideo)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonMap stream in streams)
            {
                string[] sources = StreamUrls(stream).Distinct(StringComparer.Ordinal).ToArray();
                string codec = DownloadEngine.Value(stream, "codecs");
                string mime = DownloadEngine.Value(stream, "mimeType");
                if (mime.Length == 0)
                    mime = DownloadEngine.Value(stream, "mime_type");
                bool mp4Video = mime == "video/mp4" &&
                    (codec.StartsWith("avc", StringComparison.Ordinal) || codec.StartsWith("hev", StringComparison.Ordinal) ||
                     codec.StartsWith("hvc", StringComparison.Ordinal) || codec.StartsWith("av01", StringComparison.Ordinal));
                bool aac = mime == "audio/mp4" && codec.StartsWith("mp4a", StringComparison.Ordinal);
                if (sources.Length == 0 || (isVideo ? !mp4Video : !aac) ||
                    DownloadEngine.Number(stream, "drm_tech_type") > 0)
                    continue;

                long quality = (long)DownloadEngine.Number(stream, "id");
                long codecId = (long)DownloadEngine.Number(stream, "codecid");
                string formatId = (isVideo ? "video-" : "audio-") + quality + "-" + codecId;
                if (!seen.Add(formatId))
                    continue;

                yield return new JsonMap {
                    { "format_id", formatId }, { "url", sources[0] }, { "ext", isVideo ? "mp4" : "m4a" },
                    { "bili_backup_urls", sources.Skip(1).ToArray() },
                    { "vcodec", isVideo ? codec : "none" }, { "acodec", isVideo ? "none" : codec },
                    { "width", DownloadEngine.Number(stream, "width") },
                    { "height", DownloadEngine.Number(stream, "height") },
                    { "fps", FrameRate(stream) }, { "tbr", DownloadEngine.Number(stream, "bandwidth") / 1000 }
                };
            }
        }

        private static double FrameRate(JsonMap stream)
        {
            string rate = DownloadEngine.Value(stream, "frameRate");
            if (rate.Length == 0)
                rate = DownloadEngine.Value(stream, "frame_rate");

            string[] terms = rate.Split('/');
            double first;
            if (!Double.TryParse(terms[0], NumberStyles.Float, CultureInfo.InvariantCulture, out first))
                return 0;
            if (terms.Length == 1)
                return first;

            double divisor;
            return Double.TryParse(terms[1], NumberStyles.Float, CultureInfo.InvariantCulture, out divisor) && divisor > 0
                ? first / divisor : 0;
        }

        internal static string BackupMetadata(string json, VideoQuality quality)
        {
            var metadata = DownloadEngine.Json().Deserialize<JsonMap>(json);
            bool changed = false;
            foreach (JsonMap format in Maps(Entry(metadata, "formats")))
            {
                string id = DownloadEngine.Value(format, "format_id");
                if (id != quality.VideoFormat && id != quality.AudioFormat)
                    continue;

                string backup = Items(Entry(format, "bili_backup_urls")).OfType<string>().FirstOrDefault();
                if (backup == null)
                    continue;

                format["url"] = backup;
                changed = true;
            }
            return changed ? DownloadEngine.Json().Serialize(metadata) : null;
        }

        private static IEnumerable<string> StreamUrls(JsonMap stream)
        {
            var candidates = new[] { DownloadEngine.Value(stream, "baseUrl"), DownloadEngine.Value(stream, "base_url") };
            foreach (string candidate in candidates.Concat(Items(Entry(stream, "backupUrl")).OfType<string>())
                .Concat(Items(Entry(stream, "backup_url")).OfType<string>()))
            {
                Uri media;
                if (Uri.TryCreate(candidate, UriKind.Absolute, out media) && media.Scheme == "https" &&
                    media.IsDefaultPort && media.UserInfo.Length == 0 &&
                    (media.Host.EndsWith(".bilivideo.com", StringComparison.Ordinal) ||
                     media.Host.EndsWith(".bilivideo.cn", StringComparison.Ordinal) ||
                     media.Host == "upos-hz-mirrorakam.akamaized.net"))
                    yield return media.AbsoluteUri;
            }
        }

        private static object Entry(JsonMap data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value : null;
        }

        private static IEnumerable Items(object values)
        {
            return values as IEnumerable ?? new object[0];
        }

        private static IEnumerable<JsonMap> Maps(object values)
        {
            return Items(values).OfType<JsonMap>();
        }
    }
}
