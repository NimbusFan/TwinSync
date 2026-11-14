using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace TwinSync.Native
{
    internal sealed class RequestBodyStream : Stream
    {
        private readonly Stream source;
        internal long Remaining { get; private set; }
        internal RequestBodyStream(Stream source, long length) { this.source = source; Remaining = length; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Remaining == 0 ? 0 : source.Read(buffer, offset, (int)Math.Min(count, Remaining));
            Remaining -= read; return read;
        }
        internal void DrainSmallBody()
        {
            if (Remaining == 0 || Remaining > 65536) return;
            var previous = source.CanTimeout ? source.ReadTimeout : 0;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var buffer = new byte[4096];
                while (Remaining > 0 && clock.ElapsedMilliseconds < 500)
                {
                    if (source.CanTimeout) source.ReadTimeout = Math.Max(1, 500 - (int)clock.ElapsedMilliseconds);
                    if (Read(buffer, 0, buffer.Length) == 0) break;
                }
            }
            catch (IOException) { }
            finally { if (source.CanTimeout) source.ReadTimeout = previous; }
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    internal sealed class HttpRequest
    {
        public string Method;
        public string RawPath;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ContentLength;
        public Stream Stream;
        public bool IsSecure;
        public string RemoteAddress;

        public string ReadBodyString(int maxBytes)
        {
            if (ContentLength > maxBytes) throw new Exception("请求体过大");
            if (ContentLength <= 0) return "";
            var body = new byte[ContentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = Stream.Read(body, offset, body.Length - offset);
                if (read <= 0) break;
                offset += read;
            }
            return Encoding.UTF8.GetString(body, 0, offset);
        }
    }

    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/octet-stream";
        public byte[] Body = new byte[0];
        public string FilePath;
        public Stream ContentStream;
        public long FileOffset;
        public long FileLength = -1;
        public Action CheckAccess;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpResponse Text(string text, int status)
        {
            return new HttpResponse { Status = status, ContentType = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(text ?? "") };
        }

        public static HttpResponse Json(object value, int status)
        {
            var text = JsonConvert.SerializeObject(value);
            return new HttpResponse { Status = status, ContentType = "application/json; charset=utf-8", Body = Encoding.UTF8.GetBytes(text) };
        }
    }

    internal sealed class HttpServer
    {
        internal static Action<Exception> DiagnosticError { get; set; }
        private readonly Func<HttpRequest, HttpResponse> route;
        private readonly string staticRoot;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private readonly X509Certificate2 certificate;
        private readonly SemaphoreSlim connections = new SemaphoreSlim(32, 32);
        internal Action<Exception> ReportError;

        public HttpServer(string root, Func<HttpRequest, HttpResponse> route, X509Certificate2 certificate)
        {
            staticRoot = Path.GetFullPath(root);
            this.route = route;
            this.certificate = certificate;
        }

        public void Start(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TwinSync HTTP" };
            acceptThread.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    if (!connections.Wait(0)) { client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
                catch
                {
                    if (!running) return;
                    Thread.Sleep(80);
                }
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (client)
                using (var network = client.GetStream())
                {
                    var first = new byte[1];
                    if (client.Client.Receive(first, 0, 1, SocketFlags.Peek) == 0) return;
                    var secure = first[0] == 22;
                    Stream stream = network;
                    if (secure)
                    {
                        var tls = new SslStream(network, false);
                        tls.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                        stream = tls;
                    }
                    using (stream)
                    {
                    var request = ReadRequest(stream, client);
                    if (request == null) return;
                    request.IsSecure = secure;
                    HttpResponse response = null;
                    try
                    {
                    try { response = route(request); } catch (Exception error) { response = HttpResponse.Text(error.Message, 500); }
                    if (response == null) response = Static(request);
                    // Consume small unused request bodies before close, otherwise Windows can reset a successful response.
                    ((RequestBodyStream)request.Stream).DrainSmallBody();
                    WriteResponse(stream, response);
                    }
                    finally { if (response != null && response.ContentStream != null) response.ContentStream.Dispose(); }
                    }
                }
            }
            catch (Exception error) { var diagnostic = DiagnosticError; if (diagnostic != null) diagnostic(error); if (ReportError != null) ReportError(error); }
            finally { connections.Release(); }
        }

        private HttpRequest ReadRequest(Stream stream, TcpClient client)
        {
            var header = new StringBuilder();
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0) return null;
                header.Append((char)value);
                if (header.Length > 131072) return null;
                if (header.Length >= 4)
                {
                    var len = header.Length;
                    if (header[len - 1] == '\n' && header[len - 2] == '\r' && header[len - 3] == '\n' && header[len - 4] == '\r') break;
                }
            }
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var request = new HttpRequest
            {
                Method = first[0].ToUpperInvariant(),
                RawPath = first[1],
                RemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(),
                Stream = stream
            };
            var target = request.RawPath;
            var queryIndex = target.IndexOf('?');
            if (queryIndex >= 0)
            {
                var queryText = target.Substring(queryIndex + 1);
                target = target.Substring(0, queryIndex);
                foreach (var pair in queryText.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var equals = pair.IndexOf('=');
                    var key = equals >= 0 ? pair.Substring(0, equals) : pair;
                    var value = equals >= 0 ? pair.Substring(equals + 1) : "";
                    request.Query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " "));
                }
            }
            request.Path = Uri.UnescapeDataString(target);
            for (var index = 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (string.IsNullOrEmpty(line)) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            long contentLength = 0;
            var lengthText = "";
            if (request.Headers.ContainsKey("Transfer-Encoding")) return null;
            if (request.Headers.TryGetValue("Content-Length", out lengthText) && (!long.TryParse(lengthText, out contentLength) || contentLength < 0)) return null;
            request.ContentLength = contentLength;
            request.Stream = new RequestBodyStream(stream, contentLength);
            string expect;
            if (contentLength > 0 && request.Headers.TryGetValue("Expect", out expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(interim, 0, interim.Length);
            }
            return request;
        }

        private HttpResponse Static(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "HEAD") return HttpResponse.Text("Method not allowed", 405);
            var relative = request.Path == "/" ? "index.html" : request.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relative.IndexOf("..", StringComparison.Ordinal) >= 0) return HttpResponse.Text("Not found", 404);
            var full = Path.GetFullPath(Path.Combine(staticRoot, relative));
            if (!full.StartsWith(staticRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return HttpResponse.Text("Not found", 404);
            if (!File.Exists(full)) return HttpResponse.Text("Not found", 404);
            return new HttpResponse
            {
                FilePath = full,
                ContentType = MimeType(full)
            };
        }

        private static string MimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".json": return "application/json; charset=utf-8";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        private static void WriteResponse(Stream stream, HttpResponse response)
        {
            using (var input = response.ContentStream ?? (response.FilePath == null ? null : File.OpenRead(response.FilePath)))
            {
            if (response.CheckAccess != null) response.CheckAccess();
            var statusText = response.Status == 200 ? "OK" : response.Status == 206 ? "Partial Content" : response.Status == 201 ? "Created" : response.Status == 204 ? "No Content" : response.Status == 400 ? "Bad Request" : response.Status == 401 ? "Unauthorized" : response.Status == 403 ? "Forbidden" : response.Status == 404 ? "Not Found" : response.Status == 405 ? "Method Not Allowed" : response.Status == 409 ? "Conflict" : response.Status == 423 ? "Locked" : "Error";
            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(statusText).Append("\r\n");
            header.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            var length = input == null ? response.Body.Length : response.FileLength < 0 ? input.Length - response.FileOffset : response.FileLength;
            header.Append("Content-Length: ").Append(length).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("X-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nCross-Origin-Resource-Policy: same-origin\r\n");
            header.Append("Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\n");
            header.Append("Connection: close\r\n");
            foreach (var pair in response.Headers) header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            header.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (input != null)
            {
                    input.Position = response.FileOffset;
                    var buffer = new byte[65536];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new EndOfStreamException();
                        if (response.CheckAccess != null) response.CheckAccess();
                        stream.Write(buffer, 0, read);
                        remaining -= read;
                    }
            }
            else if (response.Body.Length > 0) stream.Write(response.Body, 0, response.Body.Length);
            stream.Flush();
            }
        }
    }
}
