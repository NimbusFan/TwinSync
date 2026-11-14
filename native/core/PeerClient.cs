using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
namespace TwinSync.Native
{
    internal sealed class PeerClient
    {
        private readonly JObject peer;
        private readonly string localDeviceId;
        private readonly Action check;
        public PeerClient(JObject peer, string localDeviceId, Action check = null) { this.peer = peer; this.localDeviceId = localDeviceId; this.check = check; }
        private static HttpWebRequest Create(string host, int port, string fingerprint, string method, string route, int timeout)
        {
            Uri supplied;
            if (Uri.TryCreate(host, UriKind.Absolute, out supplied) && (supplied.Scheme == "http" || supplied.Scheme == "https")) host = supplied.Host;
            host = (host ?? "").Trim().TrimEnd('/');
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port < 1 || port > 65535) throw new Exception("设备地址或端口无效");
            var pin = DeviceSecurity.NormalizeFingerprint(fingerprint);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(new UriBuilder("https", host, port).Uri.GetLeftPart(UriPartial.Authority) + route);
            request.Method = method;
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.ServicePoint.Expect100Continue = false;
            request.ServerCertificateValidationCallback = delegate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate, System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors errors)
            {
                if (certificate == null) return false;
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
                return DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() && DeviceSecurity.Equal(DeviceSecurity.FingerprintOf(certificate), pin);
            };
            request.Headers["x-twinsync-protocol"] = "2";
            return request;
        }
        private HttpWebRequest Request(string method, string route, int timeout)
        {
            if (check != null) check();
            var request = Create(JsonUtil.GetString(peer["host"], ""), JsonUtil.GetInt(peer["port"], 55220), JsonUtil.GetString(peer["fingerprint"], ""), method, route, timeout);
            request.Headers["x-twinsync-token"] = JsonUtil.GetString(peer["token"], "");
            request.Headers["x-twinsync-device"] = localDeviceId;
            return request;
        }
        private static HttpWebResponse Response(HttpWebRequest request)
        {
            try { return (HttpWebResponse)request.GetResponse(); }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    using (var input = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var text = input.ReadToEnd();
                        try { text = JsonUtil.GetString(JObject.Parse(text)["error"], text); } catch { }
                        throw new Exception(text.Length > 300 ? text.Substring(0, 300) : text);
                    }
                }
                throw new Exception("无法建立经过设备指纹验证的加密连接，请检查地址、指纹和对方服务状态", error);
            }
        }
        private static void WriteJson(HttpWebRequest request, JToken body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = bytes.Length;
            using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
        }
        private static JObject ReadJson(HttpWebRequest request)
        {
            using (var response = Response(request))
            using (var input = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var text = input.ReadToEnd();
                return string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text);
            }
        }
        public JObject RequestJson(string route, HttpMethod method, JToken body, int timeoutMs)
        {
            var request = Request(method.Method, route, timeoutMs);
            if (body != null) WriteJson(request, body);
            return ReadJson(request);
        }
        public JObject Ping() { return RequestJson("/peer/ping", HttpMethod.Get, null, 6000); }
        public JObject Manifest(string folderId) { return RequestJson("/peer/manifest?folder=" + Uri.EscapeDataString(folderId), HttpMethod.Get, null, 45000); }
        public JObject Wake() { return RequestJson("/peer/wake", HttpMethod.Post, new JObject(), 5000); }
        public JObject Ack(string folderId, JArray entries) { return RequestJson("/peer/ack", HttpMethod.Post, JsonUtil.Obj("folder", folderId, "entries", entries), 15000); }
        public JObject Delete(string folderId, string relPath, JObject entry)
        {
            return RequestJson("/peer/delete", HttpMethod.Post, JsonUtil.Obj("folder", folderId, "path", relPath, "rev", EntryUtil.RevOf(entry), "origin", EntryUtil.OriginOf(entry), "mtimeMs", (long)EntryUtil.MtimeOf(entry)), 30000);
        }
        public JObject FolderShare(JObject folder, bool shared)
        {
            return RequestJson("/peer/folder-share", HttpMethod.Post, JsonUtil.Obj("shared", shared, "folder", JsonUtil.Obj("id", folder["id"], "name", folder["name"], "mode", folder["mode"])), 8000);
        }
        private void Copy(Stream input, Stream output, long total, Action<long, long> progress)
        {
            var buffer = new byte[65536]; long done = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); long reportedAt = 0;
            if (progress != null) progress(0, total);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (check != null) check();
                output.Write(buffer, 0, read); done += read;
                if (progress != null && clock.ElapsedMilliseconds - reportedAt >= 100) { progress(done, total); reportedAt = clock.ElapsedMilliseconds; }
            }
            if (check != null) check();
            if (done != total) throw new IOException("文件传输长度不符，未提交目标文件。");
            if (progress != null) progress(done, total);
        }

        public JObject Download(string folderId, JObject entry, string destination, Action<long, long> progress = null)
        {
            var route = "/peer/file?folder=" + Uri.EscapeDataString(folderId) + "&path=" + Uri.EscapeDataString(EntryUtil.PathOf(entry));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var temp = destination + ".twinsync-part-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var response = Response(Request("GET", route, 600000)))
                using (var input = response.GetResponseStream())
                using (var output = File.Create(temp)) Copy(input, output, EntryUtil.SizeOf(entry), progress);
                var hash = JsonUtil.Sha256File(temp);
                if (!DeviceSecurity.Equal(hash, EntryUtil.HashOf(entry))) throw new Exception("文件校验失败：" + EntryUtil.PathOf(entry));
                return JsonUtil.Obj("temp", temp, "hash", hash, "size", new FileInfo(temp).Length);
            }
            catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
        }
        public JObject Upload(string folderId, JObject entry, string source, bool resolution, Action<long, long> progress = null)
        {
            var query = "folder=" + Uri.EscapeDataString(folderId) + "&path=" + Uri.EscapeDataString(EntryUtil.PathOf(entry)) + "&rev=" + EntryUtil.RevOf(entry) + "&origin=" + Uri.EscapeDataString(EntryUtil.OriginOf(entry)) + "&mtime=" + (long)EntryUtil.MtimeOf(entry) + "&hash=" + Uri.EscapeDataString(EntryUtil.HashOf(entry)) + "&size=" + new FileInfo(source).Length + (resolution ? "&resolution=1" : "");
            var request = Request("PUT", "/peer/file?" + query, 600000);
            request.AllowWriteStreamBuffering = false;
            request.ContentType = "application/octet-stream";
            request.ContentLength = new FileInfo(source).Length;
            using (var input = File.OpenRead(source))
            using (var output = request.GetRequestStream()) Copy(input, output, request.ContentLength, progress);
            var response = ReadJson(request);
            if (check != null) check();
            return response;
        }
        public static JObject Pair(string host, int port, string code, string token, JObject device, JArray folders, string fingerprint)
        {
            var request = Create(host, port, fingerprint, "POST", "/peer/pair", 20000);
            WriteJson(request, JsonUtil.Obj("protocol", 2, "code", code, "token", token, "device", device, "folders", folders));
            return ReadJson(request);
        }
    }
}
