using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
namespace TwinSync.Native
{
    internal sealed class PeerRequestException : IOException
    {
        internal readonly int Status;
        internal PeerRequestException(string text, int status, Exception inner = null) : base(text, inner) { Status = status; }
    }
    internal sealed class PeerClient
    {
        private sealed class ResumeUnsupportedException : IOException { }
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
                        throw new PeerRequestException(text.Length > 300 ? text.Substring(0, 300) : text, (int)response.StatusCode);
                    }
                }
                throw;
            }
        }
        private static bool Transient(Exception error)
        {
            var peerError = error as PeerRequestException;
            if (peerError != null) return peerError.Status == 408 || peerError.Status == 429 || peerError.Status == 502 || peerError.Status == 503 || peerError.Status == 504;
            var web = error as WebException;
            if (web != null) return new[] { WebExceptionStatus.ConnectFailure, WebExceptionStatus.ConnectionClosed, WebExceptionStatus.Timeout, WebExceptionStatus.ReceiveFailure, WebExceptionStatus.SendFailure, WebExceptionStatus.NameResolutionFailure, WebExceptionStatus.KeepAliveFailure }.Contains(web.Status);
            return error is EndOfStreamException || error is IOException && error.InnerException is System.Net.Sockets.SocketException;
        }
        private void Backoff(int attempt, Exception error)
        {
            if (attempt >= 3 || !Transient(error)) throw error;
            for (var i = 0; i < (10 << attempt); i++) { if (check != null) check(); System.Threading.Thread.Sleep(100); }
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

        public JObject Download(string folderId, JObject entry, string destination, Action<long, long> progress = null, string cacheRoot = null)
        {
            var route = "/peer/file?folder=" + Uri.EscapeDataString(folderId) + "&path=" + Uri.EscapeDataString(EntryUtil.PathOf(entry)) + "&hash=" + Uri.EscapeDataString(EntryUtil.HashOf(entry));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var key = ResumableTransfers.Digest(peer["id"] + "|" + folderId + "|" + EntryUtil.PathOf(entry) + "|" + EntryUtil.HashOf(entry) + "|" + peer["token"]);
            var temp = TransferCache.Download(cacheRoot ?? Path.GetDirectoryName(destination), destination, key, EntryUtil.HashOf(entry));
            var total = EntryUtil.SizeOf(entry); var attempt = 0; var verifyPrefix = true;
            using (var output = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (output.Length > total) output.SetLength(0);
                if (progress != null) progress(output.Length, total);
                // A completed partial must be checked again after a process restart.
                if (output.Length == total) { output.Position = 0; if (!DeviceSecurity.Equal(ResumableTransfers.PrefixHash(output, total), EntryUtil.HashOf(entry))) { output.SetLength(0); if (progress != null) progress(0, total); } }
                while (output.Length < total)
                {
                    var offset = output.Length;
                    var checkpoint = offset;
                    try
                    {
                        var request = Request("GET", route, 30000);
                        var end = Math.Min(total, offset + ResumableTransfers.ChunkSize) - 1; request.AddRange(offset, end);
                        if (verifyPrefix) { output.Position = 0; request.Headers["x-twinsync-prefix"] = ResumableTransfers.PrefixHash(output, offset); }
                        using (var response = Response(request))
                        using (var input = response.GetResponseStream())
                        {
                            if (response.StatusCode == HttpStatusCode.OK) { output.SetLength(0); Copy(input, output, total, progress); }
                            else
                            {
                                if ((int)response.StatusCode != 206 || response.Headers["Content-Range"] != "bytes " + offset + "-" + end + "/" + total) throw new IOException("对方返回的续传范围无效。");
                                var buffer = new byte[(int)(end - offset + 1)]; var done = 0;
                                while (done < buffer.Length) { if (check != null) check(); var n = input.Read(buffer, done, buffer.Length - done); if (n == 0) throw new EndOfStreamException("下载中断。"); done += n; }
                                string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = ResumableTransfers.Hex(sha.ComputeHash(buffer));
                                if (!DeviceSecurity.Equal(hash, response.Headers["x-twinsync-block-hash"] ?? "")) throw new IOException("下载分块校验失败。");
                                if (check != null) check(); output.Position = offset; output.Write(buffer, 0, buffer.Length); output.Flush(true);
                                checkpoint = output.Length;
                                if (progress != null) progress(output.Length, total);
                            }
                        }
                        verifyPrefix = false; attempt = 0;
                    }
                    catch (Exception error)
                    {
                        output.SetLength(checkpoint); output.Flush(true);
                        var peerError = error as PeerRequestException;
                        if (peerError != null && peerError.Status == 409 && offset > 0 && verifyPrefix) { output.SetLength(0); if (progress != null) progress(0, total); verifyPrefix = true; continue; }
                        Backoff(attempt++, error); verifyPrefix = true;
                    }
                }
            }
            var finalHash = JsonUtil.Sha256File(temp);
            if (!DeviceSecurity.Equal(finalHash, EntryUtil.HashOf(entry))) { File.Delete(temp); TransferCache.ForgetDownload(temp); throw new IOException("文件校验失败：" + EntryUtil.PathOf(entry)); }
            return JsonUtil.Obj("temp", temp, "hash", finalHash, "size", total);
        }
        public JObject Upload(string folderId, JObject entry, string source, bool resolution, Action<long, long> progress = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return UploadResumable(folderId, entry, source, progress); }
                catch (ResumeUnsupportedException) { return UploadLegacy(folderId, entry, source, resolution, progress); }
                catch (PeerRequestException error) { if (error.Status == 404 && attempt < 3) { if (check != null) check(); System.Threading.Thread.Sleep(100); continue; } Backoff(attempt, error); }
                catch (Exception error) { Backoff(attempt, error); }
            }
        }
        private JObject UploadResumable(string folderId, JObject entry, string source, Action<long, long> progress)
        {
            // Open the source before allocating a receiver task. Allow atomic replacement while this snapshot is being sent.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (input.Length != EntryUtil.SizeOf(entry)) throw new IOException("源文件大小已变化，请重新核对。");
                var body = JsonUtil.Obj("folder", folderId, "entry", entry.DeepClone());
                JObject state;
                try { state = RequestJson("/peer/transfers/start", HttpMethod.Post, body, 600000); }
                catch (PeerRequestException error) { if (error.Status == 404 || error.Status == 405 || error.Status == 501) throw new ResumeUnsupportedException(); throw; }
                if (JsonUtil.GetBool(state["complete"], false)) { if (progress != null) progress(EntryUtil.SizeOf(entry), EntryUtil.SizeOf(entry)); return state; }
                var offset = (long)state["offset"]; var total = EntryUtil.SizeOf(entry);
                if (offset < 0 || offset > total) throw new IOException("续传位置无效。");
                if (!DeviceSecurity.Equal(ResumableTransfers.PrefixHash(input, offset, check), (string)state["prefixHash"])) { body["reset"] = true; state = RequestJson("/peer/transfers/start", HttpMethod.Post, body, 600000); offset = 0; }
                input.Position = offset; if (progress != null) progress(offset, total);
                var buffer = new byte[ResumableTransfers.ChunkSize]; var id = (string)state["id"];
                while (offset < total)
                {
                    var count = (int)Math.Min(buffer.Length, total - offset); var read = 0;
                    while (read < count) { if (check != null) check(); var n = input.Read(buffer, read, count - read); if (n == 0) throw new IOException("源文件在传输时被截断。"); read += n; }
                    string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = ResumableTransfers.Hex(sha.ComputeHash(buffer, 0, count));
                    var request = Request("PUT", "/peer/transfers/" + id + "?offset=" + offset + "&hash=" + hash, 30000);
                    request.AllowWriteStreamBuffering = false; request.ContentType = "application/octet-stream"; request.ContentLength = count;
                    using (var output = request.GetRequestStream()) for (var i = 0; i < count; i += 65536) { if (check != null) check(); output.Write(buffer, i, Math.Min(65536, count - i)); if (progress != null) progress(offset + Math.Min(i + 65536, count), total); }
                    var ack = ReadJson(request); if ((long)ack["offset"] != offset + count) throw new IOException("分块确认位置无效。"); offset += count;
                }
                if (check != null) check();
                return RequestJson("/peer/transfers/" + id + "/commit", HttpMethod.Post, new JObject(), 600000);
            }
        }
        private JObject UploadLegacy(string folderId, JObject entry, string source, bool resolution, Action<long, long> progress)
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
