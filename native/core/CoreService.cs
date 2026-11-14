using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class CoreService
    {
        public ConfigStore Config { get; private set; }
        public string AppDir { get; private set; }
        public string PublicDir { get; private set; }
        public int Port { get; private set; }
        public DateTime StartedAt { get; private set; }
        public Dictionary<string, IndexStore> Indexes { get; private set; }
        public VersionStore Versions { get; private set; }
        public SyncEngine Engine { get; private set; }
        private HttpServer server;
        private string pairingCode;
        private DateTime pairingExpires;
        private Timer autoTimer;
        private Timer watchTimer;
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly object watcherGate = new object();
        private readonly Dictionary<string, object> shareGates = new Dictionary<string, object>();
        private int watchDirty;
        private DateTime lastAutoAt = DateTime.MinValue;
        private readonly object pairingGate = new object();
        public DeviceSecurity Security { get; private set; }
        private int pairingFailures;
        private DateTime pairingWindow = DateTime.UtcNow;
        private readonly Action<string> revealPath;
        private readonly Action<bool> registerStartup;
        public string StartupError { get; private set; }

        public CoreService(string appDir, string home, int? port, Action<string> reveal = null, Action<bool> startup = null)
        {
            revealPath = reveal ?? ExplorerIntegration.Show;
            AppDir = appDir;
            PublicDir = Path.Combine(appDir, "public");
            Config = new ConfigStore(home);
            Security = new DeviceSecurity(home);
            Port = port.HasValue ? port.Value : Config.Port;
            registerStartup = startup ?? (enabled => StartupRegistration.SetEnabled(enabled, Path.Combine(appDir, "TwinSync.exe"), Config.Home, Port));
            StartedAt = DateTime.UtcNow;
            NewPairingCode();
            Indexes = new Dictionary<string, IndexStore>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Config.Folders.OfType<JObject>())
            {
                var id = JsonUtil.GetString(folder["id"], "");
                if (!string.IsNullOrEmpty(id)) Indexes[id] = new IndexStore(Config.StateDir, id);
            }
            Versions = new VersionStore(Config);
            Engine = new SyncEngine(Config, Indexes, Versions, ValidatePairCode, Security.Fingerprint);
        }

        public void Start()
        {
            server = new HttpServer(PublicDir, Route, Security.Certificate);
            server.Start(Port);
            StartAutomation();
        }

        public void Stop()
        {
            if (server != null) server.Stop();
            if (autoTimer != null) autoTimer.Dispose();
            if (watchTimer != null) watchTimer.Dispose();
            lock (watcherGate)
            {
                foreach (var watcher in watchers)
                {
                    try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
                }
                watchers.Clear();
            }
        }

        private void StartAutomation()
        {
            autoTimer = new Timer(delegate { AutoTick(); }, null, 5000, 5000);
            watchTimer = new Timer(delegate { WatchTick(); }, null, 1500, 1500);
            RebuildWatchers();
        }

        private void WatchFolder(JObject folder)
        {
            if (!JsonUtil.GetBool(folder["enabled"], true) || JsonUtil.GetBool(folder["paused"], false)) return;
            var path = JsonUtil.GetString(folder["path"], "");
            if (!Directory.Exists(path)) return;
            try
            {
                var watcher = new FileSystemWatcher(path);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += delegate { Interlocked.Exchange(ref watchDirty, 1); };
                watcher.Created += delegate { Interlocked.Exchange(ref watchDirty, 1); };
                watcher.Deleted += delegate { Interlocked.Exchange(ref watchDirty, 1); };
                watcher.Renamed += delegate { Interlocked.Exchange(ref watchDirty, 1); };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch { }
        }

        private void AutoTick()
        {
            try { Versions.Maintain(); } catch (Exception error) { Engine.AddActivity("version", "版本清理未完成", error.Message, "warn"); }
            if (JsonUtil.GetBool(Config.Settings["syncPaused"], false)) return;
            if (!JsonUtil.GetBool(Config.Settings["autoSync"], true)) return;
            if (Config.Peers.Count == 0) return;
            var interval = JsonUtil.GetInt(Config.Settings["syncIntervalSec"], 30);
            if ((DateTime.UtcNow - lastAutoAt).TotalSeconds < interval) return;
            lastAutoAt = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate { Engine.SyncAll(false); });
        }

        private void RebuildWatchers()
        {
            List<JObject> folders;
            lock (Engine.ConfigurationGate) folders = Config.Folders.OfType<JObject>().ToList();
            lock (watcherGate)
            {
                foreach (var watcher in watchers) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { } }
                watchers.Clear();
                foreach (var folder in folders) WatchFolder(folder);
            }
        }

        private void WatchTick()
        {
            if (Interlocked.Exchange(ref watchDirty, 0) == 0) return;
            if (JsonUtil.GetBool(Config.Settings["syncPaused"], false)) return;
            List<JObject> snapshot;
            lock (Engine.ConfigurationGate) snapshot = Config.Folders.OfType<JObject>().ToList();
            foreach (var folder in snapshot)
            {
                try { Engine.ScanFolder(folder, false); } catch { }
            }
            if (Config.Peers.Count > 0 && JsonUtil.GetBool(Config.Settings["autoSync"], true)) ThreadPool.QueueUserWorkItem(delegate { Engine.SyncAll(false); });
        }

        private HttpResponse Route(HttpRequest request)
        {
            if (request.Path.StartsWith("/peer/", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.IsSecure) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "设备通信必须使用 TLS 加密连接，请更新两端程序"), 403);
                return PeerRoute(request);
            }
            var denied = AuthorizePanel(request);
            if (denied != null) return denied;
            if (request.Path == "/" || request.Path == "/index.html")
            {
                var page = HttpResponse.Text(File.ReadAllText(Path.Combine(PublicDir, "index.html"), Encoding.UTF8).Replace("__CSRF_TOKEN__", Security.CsrfToken), 200);
                page.ContentType = "text/html; charset=utf-8";
                return page;
            }
            if (request.Path == "/api/health") return HttpResponse.Json(JsonUtil.Obj("ok", true, "app", "TwinSync", "version", "1.0.0", "port", Port, "deviceId", Config.DeviceId), 200);
            if (request.Path == "/api/state") return HttpResponse.Json(BuildState(), 200);
            if (request.Path == "/api/events") return Events();
            if (request.Path == "/api/settings" && (request.Method == "PATCH" || request.Method == "POST")) return UpdateSettings(request);
            if (request.Path == "/api/device" && request.Method == "POST") return UpdateDevice(request);
            if (request.Path == "/api/pairing/refresh" && request.Method == "POST")
            {
                NewPairingCode();
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "pairing", PairingState()), 200);
            }
            if (request.Path == "/api/peers/pair" && request.Method == "POST") return PairOutgoing(request);
            if (request.Path == "/api/sync" && request.Method == "POST")
            {
                try
                {
                    var text = request.ReadBodyString(1024 * 1024);
                    var body = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                    foreach (var key in new[] { "folderId", "peerId" })
                        if (body[key] != null && body[key].Type != JTokenType.Null && (body[key].Type != JTokenType.String || string.IsNullOrWhiteSpace(body[key].ToString()))) throw new Exception("同步范围无效，请重新选择文件夹或设备。");
                    var result = Engine.QueueSync(true, JsonUtil.GetString(body["folderId"], null), JsonUtil.GetString(body["peerId"], null));
                    result["ok"] = true;
                    return HttpResponse.Json(result, JsonUtil.GetBool(result["accepted"], false) ? 202 : 200);
                }
                catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
            }
            if (request.Path == "/api/scan" && request.Method == "POST")
            {
                foreach (var folder in Config.Folders.OfType<JObject>()) Engine.ScanFolder(folder, true);
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            if (request.Path == "/api/fs/list" && request.Method == "GET") return ListDirectories(request);
            if (request.Path == "/api/shutdown" && request.Method == "POST")
            {
                Timer timer = null;
                timer = new Timer(delegate { try { timer.Dispose(); } catch { } Environment.Exit(0); }, null, 250, Timeout.Infinite);
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            if (request.Path == "/api/folders" && request.Method == "POST") return AddFolder(request);
            var peerMatch = Regex.Match(request.Path, "^/api/peers/([0-9a-fA-F-]{36})(?:/([a-z-]+))?$");
            if (peerMatch.Success)
            {
                var peerId = peerMatch.Groups[1].Value;
                var peerAction = peerMatch.Groups[2].Success ? peerMatch.Groups[2].Value : "";
                var peer = Config.GetPeer(peerId);
                if (peer == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "设备不存在"), 404);
                if (peerAction == "test" && request.Method == "POST") return HttpResponse.Json(JsonUtil.Obj("ok", true, "online", Engine.TestPeer(peer)), 200);
                if (peerAction == "folders" && request.Method == "POST") return PeerFolders(request, peer);
                if (request.Method == "PATCH") return PeerPatch(request, peer);
                if (request.Method == "DELETE") return PeerRemove(peer);
            }
            var conflictMatch = Regex.Match(request.Path, "^/api/conflicts/([0-9a-fA-F-]{36})/([0-9a-fA-F-]{36})/resolve$");
            if (conflictMatch.Success && request.Method == "POST") return ResolveConflict(request, conflictMatch.Groups[1].Value, conflictMatch.Groups[2].Value);
            if (request.Path == "/api/reveal" && request.Method == "POST") return RevealGlobal(request);
            var folderMatch = Regex.Match(request.Path, "^/api/folders/([0-9a-fA-F-]{36})(?:/([a-z-]+))?$");
            if (folderMatch.Success)
            {
                var folderId = folderMatch.Groups[1].Value;
                var action = folderMatch.Groups[2].Success ? folderMatch.Groups[2].Value : "";
                var folder = Config.GetFolder(folderId);
                if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件夹不存在"), 404);
                if (action == "files" && request.Method == "GET") return ListFiles(request, folder);
                if (action == "browse" && request.Method == "GET") return BrowseFolder(request, folder);
                if (action == "download" && request.Method == "GET") return DownloadFile(request, folder);
                if (action == "reveal" && request.Method == "POST") return Reveal(request, folder);
                if (request.Method == "PATCH") return UpdateFolder(request, folder);
                if (request.Method == "DELETE") return RemoveFolder(folder);
            }
            if (request.Path == "/api/versions" && request.Method == "GET") return ListVersions(request);
            var versionContent = Regex.Match(request.Path, "^/api/versions/([0-9a-fA-F-]{36})/content$");
            if (versionContent.Success && request.Method == "GET") return VersionContent(versionContent.Groups[1].Value);
            var versionRestore = Regex.Match(request.Path, "^/api/versions/([0-9a-fA-F-]{36})/restore$");
            if (versionRestore.Success && request.Method == "POST") return RestoreVersion(versionRestore.Groups[1].Value);
            if (request.Path == "/api/versions/restore-batch" && request.Method == "POST") return RestoreBatch(request);
            if (request.Path == "/api/activity" && request.Method == "GET") return HttpResponse.Json(JsonUtil.Obj("ok", true, "activity", Engine.Activity), 200);
            if (request.Path == "/api/conflicts" && request.Method == "GET") return HttpResponse.Json(JsonUtil.Obj("ok", true, "conflicts", ((JObject)BuildState())["conflicts"]), 200);
            return null;
        }

        private HttpResponse AuthorizePanel(HttpRequest request)
        {
            string host;
            Uri authority;
            IPAddress address;
            if (!request.Headers.TryGetValue("Host", out host) || !Uri.TryCreate((request.IsSecure ? "https://" : "http://") + host, UriKind.Absolute, out authority) || authority.Port != Port || (authority.Host != "localhost" && !IPAddress.TryParse(authority.Host, out address))) return HttpResponse.Text("Invalid Host", 403);
            var local = IPAddress.TryParse(request.RemoteAddress, out address) && IPAddress.IsLoopback(address);
            if (!local)
            {
                if (!JsonUtil.GetBool(Config.Settings["allowRemoteUi"], false) || !request.IsSecure) return HttpResponse.Text("管理面板仅允许本机访问；远程访问需要开启设置并使用加密管理链接", 403);
                string access;
                if (request.Query.TryGetValue("access", out access) && DeviceSecurity.Equal(access, Security.RemoteToken))
                {
                    var redirect = HttpResponse.Text("", 302);
                    redirect.Headers["Location"] = "/";
                    redirect.Headers["Set-Cookie"] = "twinsync-admin=" + Security.RemoteToken + "; Path=/; HttpOnly; Secure; SameSite=Strict";
                    return redirect;
                }
                string cookie;
                if (!request.Headers.TryGetValue("Cookie", out cookie) || !cookie.Split(';').Any(part => DeviceSecurity.Equal(part.Trim(), "twinsync-admin=" + Security.RemoteToken))) return HttpResponse.Text("请使用本机设备页生成的加密管理链接", 401);
            }
            string origin;
            if (request.Headers.TryGetValue("Origin", out origin) && !DeviceSecurity.Equal(origin, authority.GetLeftPart(UriPartial.Authority))) return HttpResponse.Text("跨站请求已拒绝", 403);
            string fetchSite;
            if (request.Headers.TryGetValue("Sec-Fetch-Site", out fetchSite) && fetchSite == "cross-site") return HttpResponse.Text("跨站请求已拒绝", 403);
            if (request.Path.StartsWith("/api/") && request.Method != "GET" && request.Method != "HEAD")
            {
                string csrf;
                if (!request.Headers.TryGetValue("x-twinsync-csrf", out csrf) || !DeviceSecurity.Equal(csrf, Security.CsrfToken)) return HttpResponse.Text("操作校验失败，请刷新页面", 403);
            }
            return null;
        }

        private void ValidateFolder(JObject folder)
        {
            var path = JsonUtil.GetString(folder["path"], "");
            SafeFiles.Join(path, "");
            var selected = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var privateHome = Path.GetFullPath(Config.Home).TrimEnd('\\') + "\\";
            if (selected.StartsWith(privateHome, StringComparison.OrdinalIgnoreCase) || privateHome.StartsWith(selected, StringComparison.OrdinalIgnoreCase)) throw new Exception("同步目录不能包含 TwinSync 的身份、配置和版本数据");
            if (!new[] { "two-way", "send-only", "receive-only" }.Contains(JsonUtil.GetString(folder["mode"], "two-way"))) throw new Exception("同步方向无效");
            var peerIds = folder["peerIds"] as JArray ?? new JArray();
            if (peerIds.Any(id => Config.GetPeer(id.ToString()) == null)) throw new Exception("只能与已配对设备共享");
        }

        private void NotifyShares(JObject folder, JArray previous)
        {
            lock (Engine.ConfigurationGate)
            {
                var current = (folder["peerIds"] as JArray) ?? new JArray();
                foreach (var peer in Config.Peers.OfType<JObject>().ToList())
                {
                    var id = peer["id"].ToString(); var folderId = folder["id"].ToString();
                    if (!current.Any(value => value.ToString() == id) && !previous.Any(value => value.ToString() == id)) continue;
                    var key = folderId + ":" + id; object serial;
                    if (!shareGates.TryGetValue(key, out serial)) { serial = new object(); shareGates[key] = serial; }
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        lock (serial)
                        {
                            try
                            {
                                JObject snapshot, destination; bool shared;
                                lock (Engine.ConfigurationGate)
                                {
                                    if (!object.ReferenceEquals(Config.GetPeer(id), peer)) return;
                                    var latest = Config.GetFolder(folderId);
                                    shared = latest != null && ((latest["peerIds"] as JArray) ?? new JArray()).Any(value => value.ToString() == id);
                                    snapshot = (JObject)(latest ?? folder).DeepClone(); destination = (JObject)peer.DeepClone();
                                }
                                new PeerClient(destination, Config.DeviceId).FolderShare(snapshot, shared);
                            }
                            catch { }
                        }
                    });
                }
            }
        }

        private bool SharedWith(JObject folder, JObject peer)
        {
            return folder != null && JsonUtil.GetBool(folder["enabled"], true) && !JsonUtil.GetBool(folder["paused"], false) && ((folder["peerIds"] as JArray) ?? new JArray()).Any(id => id.ToString() == JsonUtil.GetString(peer["id"], ""));
        }

        private HttpResponse PeerRoute(HttpRequest request)
        {
            if (request.Path == "/peer/pair" && request.Method == "POST")
            {
                try
                {
                    var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                    return HttpResponse.Json(Engine.PairIncoming(body, request.RemoteAddress), 200);
                }
                catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
            }
            var peer = AuthenticatePeer(request);
            if (peer == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "未授权的设备"), 401);
            try
            {
                if (request.Path == "/peer/ping")
                {
                    var folders = new JArray(Config.Folders.OfType<JObject>().Where(folder => SharedWith(folder, peer)).Select(folder => JsonUtil.Obj("id", folder["id"], "name", folder["name"], "mode", folder["mode"], "shared", true)));
                    return HttpResponse.Json(JsonUtil.Obj("ok", true, "device", JsonUtil.Obj("id", Config.DeviceId, "name", Config.DeviceName), "folders", folders), 200);
                }
                if (request.Path == "/peer/manifest" && request.Method == "GET")
                {
                    var folder = Config.GetFolder(request.Query.ContainsKey("folder") ? request.Query["folder"] : "");
                    if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "对方没有这个文件夹"), 404);
                    if (!SharedWith(folder, peer)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "未共享"), 403);
                    return HttpResponse.Json(JsonUtil.Obj("ok", true, "folder", JsonUtil.Obj("id", folder["id"], "name", folder["name"]), "entries", Engine.Manifest(folder, peer)), 200);
                }
                if (request.Path == "/peer/file" && request.Method == "GET") return PeerDownload(peer, request);
                if (request.Path == "/peer/file" && request.Method == "PUT") return PeerUpload(peer, request);
                if (request.Path == "/peer/ack" && request.Method == "POST")
                {
                    var body = JObject.Parse(request.ReadBodyString(4 * 1024 * 1024));
                    var folder = Config.GetFolder(JsonUtil.GetString(body["folder"], ""));
                    if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件夹不存在"), 404);
                    if (!SharedWith(folder, peer)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "未共享"), 403);
                    Engine.ApplyAcks(folder, body["entries"] as JArray, peer);
                    return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
                }
                if (request.Path == "/peer/delete" && request.Method == "POST")
                {
                    var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                    var folder = Config.GetFolder(JsonUtil.GetString(body["folder"], ""));
                    if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件夹不存在"), 404);
                    if (!SharedWith(folder, peer) || JsonUtil.GetString(folder["mode"], "two-way") == "send-only") return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "没有写入权限"), 403);
                    var incoming = JsonUtil.Obj("path", body["path"], "deleted", true, "rev", body["rev"], "origin", body["origin"], "mtimeMs", body["mtimeMs"], "size", 0, "hash", null);
                    return HttpResponse.Json(Engine.AcceptIncomingDelete(peer, folder, JsonUtil.GetString(body["path"], ""), incoming), 200);
                }
                if (request.Path == "/peer/wake" && request.Method == "POST")
                {
                    ThreadPool.QueueUserWorkItem(delegate { Engine.SyncAll(false); });
                    return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
                }
                if (request.Path == "/peer/folder-share" && request.Method == "POST")
                {
                    var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                    return HttpResponse.Json(Engine.FolderShare(peer, body), 200);
                }
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 500); }
            return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "接口不存在"), 404);
        }

        private JObject AuthenticatePeer(HttpRequest request)
        {
            string token;
            string id;
            if (!request.Headers.TryGetValue("x-twinsync-token", out token) || string.IsNullOrEmpty(token) || !request.Headers.TryGetValue("x-twinsync-device", out id)) return null;
            lock (Engine.ConfigurationGate) return Config.Peers.OfType<JObject>().FirstOrDefault(peer => Regex.IsMatch(JsonUtil.GetString(peer["fingerprint"], ""), "^[0-9A-F]{64}$") && !JsonUtil.GetBool(peer["paused"], false) && JsonUtil.GetString(peer["id"], "") == id && DeviceSecurity.Equal(JsonUtil.GetString(peer["token"], ""), token));
        }

        private HttpResponse PeerDownload(JObject peer, HttpRequest request)
        {
            var folder = Config.GetFolder(request.Query.ContainsKey("folder") ? request.Query["folder"] : "");
            if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件夹不存在"), 404);
            if (!SharedWith(folder, peer)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "未共享"), 403);
            var entry = Engine.IndexFor(folder).Get(request.Query.ContainsKey("path") ? request.Query["path"] : "");
            if (entry == null || EntryUtil.Deleted(entry)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件不存在"), 404);
            var path = SafePath(JsonUtil.GetString(folder["path"], ""), EntryUtil.PathOf(entry));
            if (!File.Exists(path)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件不存在"), 404);
            var response = new HttpResponse { FilePath = path, ContentType = "application/octet-stream", CheckAccess = Engine.TransferGuard(peer, folder) };
            response.Headers["x-twinsync-hash"] = EntryUtil.HashOf(entry);
            response.Headers["x-twinsync-mtime"] = ((long)EntryUtil.MtimeOf(entry)).ToString();
            response.Headers["x-twinsync-size"] = new FileInfo(path).Length.ToString();
            return response;
        }

        private HttpResponse PeerUpload(JObject peer, HttpRequest request)
        {
            var folder = Config.GetFolder(request.Query.ContainsKey("folder") ? request.Query["folder"] : "");
            if (folder == null) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件夹不存在"), 404);
            if (!SharedWith(folder, peer)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "未共享"), 403);
            var relPath = request.Query.ContainsKey("path") ? request.Query["path"].Replace('\\', '/') : "";
            if (JsonUtil.GetString(folder["mode"], "two-way") == "send-only") return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "该目录仅允许发送"), 403);
            if (string.IsNullOrEmpty(relPath)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "缺少路径"), 400);
            var target = SafePath(JsonUtil.GetString(folder["path"], ""), relPath);
            var temp = Path.Combine(JsonUtil.GetString(folder["path"], ""), ".twinsync-part-" + Guid.NewGuid().ToString("N"));
            var check = Engine.TransferGuard(peer, folder, true, relPath);
            try
            {
                check();
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var output = File.Create(temp))
                {
                    var buffer = new byte[65536];
                    var remaining = request.ContentLength;
                    while (remaining > 0)
                    {
                        var read = request.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) break;
                        check();
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    if (remaining != 0) throw new Exception("上传中断，未写入目标文件");
                }
                var hash = JsonUtil.Sha256File(temp);
                var expected = request.Query.ContainsKey("hash") ? request.Query["hash"] : "";
                if (!Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$") || !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)) throw new Exception("上传校验失败");
                var incoming = JsonUtil.Obj(
                    "path", relPath,
                    "type", "file",
                    "size", new FileInfo(temp).Length,
                    "mtimeMs", request.Query.ContainsKey("mtime") ? request.Query["mtime"] : "0",
                    "hash", hash,
                    "rev", request.Query.ContainsKey("rev") ? request.Query["rev"] : "0",
                    "origin", request.Query.ContainsKey("origin") ? request.Query["origin"] : JsonUtil.GetString(peer["id"], ""),
                    "deleted", false,
                    "baseHash", "",
                    "updatedAt", JsonUtil.NowIso());
                var result = Engine.AcceptIncomingFile(peer, folder, relPath, incoming, temp, check);
                return HttpResponse.Json(result, 200);
            }
            catch (Exception error)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400);
            }
        }

        private HttpResponse PairOutgoing(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var peer = Engine.PairOutgoing(JsonUtil.GetString(body["host"], ""), JsonUtil.GetInt(body["port"], 55220), JsonUtil.GetString(body["code"], ""), JsonUtil.GetString(body["fingerprint"], ""));
                var publicPeer = (JObject)peer.DeepClone(); publicPeer.Remove("token");
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "peer", publicPeer), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse Events()
        {
            var response = HttpResponse.Text("", 200);
            response.ContentType = "text/event-stream";
            response.Body = Encoding.UTF8.GetBytes("event: state\ndata: " + JsonConvert.SerializeObject(BuildState()) + "\n\n");
            return response;
        }

        private HttpResponse UpdateSettings(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                if (body["openAtLogin"] != null && body["openAtLogin"].Type != JTokenType.Boolean) throw new Exception("开机启动设置必须为开启或关闭。");
                if (body["conflictPolicy"] != null && !new[] { "copy", "newest" }.Contains(body["conflictPolicy"].ToString())) throw new Exception("冲突处理策略无效。");
                foreach (var key in new[] { "maxVersionsPerFile", "versionRetentionDays" })
                    if (body[key] != null && (body[key].Type != JTokenType.Integer || JsonUtil.GetInt(body[key], 0) < 1 || JsonUtil.GetInt(body[key], int.MaxValue) > (key == "maxVersionsPerFile" ? 200 : 3650))) throw new Exception("历史版本保留设置超出范围。");
                lock (Engine.ConfigurationGate)
                {
                    var previous = (JObject)Config.Settings.DeepClone();
                    var startupChanged = body["openAtLogin"] != null;
                    if (startupChanged) registerStartup(body["openAtLogin"].Value<bool>());
                    try
                    {
                        foreach (var property in body.Properties())
                        {
                            if (property.Name == "theme" && !new[] { "auto", "light", "dark" }.Contains(property.Value.ToString())) continue;
                            Config.Settings[property.Name] = property.Value.DeepClone();
                        }
                        Config.Save();
                        if (new[] { "syncPaused", "conflictPolicy" }.Any(key => body[key] != null && !JToken.DeepEquals(previous[key], body[key]))) Engine.InvalidatePause();
                        if (startupChanged) StartupError = null;
                    }
                    catch
                    {
                        Config.Root["settings"] = previous;
                        if (startupChanged) { try { registerStartup(JsonUtil.GetBool(previous["openAtLogin"], false)); } catch { } }
                        throw;
                    }
                }
                if (body["maxVersionsPerFile"] != null || body["versionRetentionDays"] != null)
                    try { Versions.Maintain(true); } catch (Exception error) { Engine.AddActivity("version", "版本清理未完成", error.Message, "warn"); }
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "settings", Config.Settings), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        public void ReconcileStartup()
        {
            try { registerStartup(JsonUtil.GetBool(Config.Settings["openAtLogin"], false)); StartupError = null; }
            catch (Exception error) { StartupError = error.Message; }
        }

        private HttpResponse UpdateDevice(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var name = body["name"] == null ? null : body["name"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new Exception("请填写有效的本机名称");
                Config.Device["name"] = name.Substring(0, Math.Min(64, name.Length));
                Config.Device["nameConfigured"] = true;
                Config.Save();
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "device", DeviceSecurity.PublicDevice(Config.Device)), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse AddFolder(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                JObject folder = null;
                Engine.ChangeConfiguration(delegate
                {
                    ValidateFolder(body);
                    var offeredId = JsonUtil.GetString(body["offerId"], "");
                    if (offeredId.Length > 0 && !Engine.PendingFolders().OfType<JObject>().Any(item => JsonUtil.GetString(item["id"], "") == offeredId)) throw new Exception("共享邀请已失效");
                    folder = JsonUtil.Obj(
                        "id", offeredId.Length > 0 ? offeredId : JsonUtil.RandomId(),
                        "name", JsonUtil.GetString(body["name"], "同步文件夹"),
                        "path", JsonUtil.GetString(body["path"], ""),
                        "mode", JsonUtil.GetString(body["mode"], "two-way"),
                        "enabled", true,
                        "paused", JsonUtil.GetBool(body["paused"], false),
                        "peerIds", body["peerIds"] is JArray ? body["peerIds"] : new JArray(),
                        "ignore", body["ignore"] is JArray ? body["ignore"] : new JArray(),
                        "createdAt", JsonUtil.NowIso());
                    if (Config.GetFolder(JsonUtil.GetString(folder["id"], "")) != null) throw new Exception("文件夹已添加");
                    Directory.CreateDirectory(JsonUtil.GetString(folder["path"], ""));
                    Config.Folders.Add(folder);
                    Indexes[JsonUtil.GetString(folder["id"], "")] = new IndexStore(Config.StateDir, JsonUtil.GetString(folder["id"], ""));
                    Config.Save();
                }, false);
                NotifyShares(folder, new JArray());
                RebuildWatchers();
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "folder", folder), 201);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse UpdateFolder(HttpRequest request, JObject folder)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                JArray previous = null;
                var simpleChange = body.Properties().All(property => property.Name == "name" || property.Name == "paused" || property.Name == "peerIds" || JToken.DeepEquals(folder[property.Name], property.Value) || (property.Name == "offerId" && string.IsNullOrEmpty(property.Value.ToString())));
                Engine.ChangeConfiguration(delegate
                {
                    var candidate = (JObject)folder.DeepClone();
                    foreach (var property in body.Properties()) candidate[property.Name] = property.Value.DeepClone();
                    ValidateFolder(candidate);
                    previous = (JArray)(((folder["peerIds"] as JArray) ?? new JArray()).DeepClone());
                    var permissionsChanged = body.Properties().Any(property => new[] { "paused", "peerIds", "mode", "path", "enabled" }.Contains(property.Name) && !JToken.DeepEquals(folder[property.Name], property.Value));
                    var oldPath = JsonUtil.GetString(folder["path"], "");
                    var newPath = JsonUtil.GetString(candidate["path"], "");
                    var pathChanged = !FolderMigration.Normalize(oldPath).Equals(FolderMigration.Normalize(newPath), StringComparison.OrdinalIgnoreCase);
                    if (pathChanged)
                    {
                        if (JsonUtil.GetString(body["pathChangeMode"], "") != "migrate") throw new Exception("更换目录需要先确认迁移；若要同步另一组文件，请单独添加同步文件夹。");
                        FolderMigration.CopyAndVerify(oldPath, newPath, Engine.IndexFor(folder));
                    }
                    else Directory.CreateDirectory(newPath);
                    foreach (var property in body.Properties())
                    {
                        if (property.Name == "id" || property.Name == "createdAt" || property.Name == "pathChangeMode") continue;
                        folder[property.Name] = property.Value.DeepClone();
                    }
                    if (pathChanged)
                    {
                        folder["paused"] = true;
                        var index = Engine.IndexFor(folder);
                        index.LastSyncAt = null;
                        index.Root["syncState"] = "waiting";
                        index.Root["lastError"] = null;
                        index.Save();
                    }
                    Config.Save();
                    if (permissionsChanged) Engine.InvalidateFolder(folder);
                    RebuildWatchers();
                    if (pathChanged) { Engine.Status["lastResult"] = "configuration-changed"; Engine.SetStatus("waiting", "目录已迁移，请核对后恢复同步", null); }
                }, !simpleChange);
                NotifyShares(folder, previous);
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "folder", folder), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse RemoveFolder(JObject folder)
        {
            Engine.ChangeConfiguration(delegate { Engine.InvalidateFolder(folder); Config.Folders.Remove(folder); Config.Save(); }, false);
            RebuildWatchers();
            return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
        }

        private HttpResponse ListFiles(HttpRequest request, JObject folder)
        {
            var index = Engine.IndexFor(folder);
            var query = request.Query.ContainsKey("query") ? request.Query["query"].Trim() : "";
            var status = request.Query.ContainsKey("status") ? request.Query["status"] : "all";
            int limit, offset;
            if (!request.Query.ContainsKey("limit") || !int.TryParse(request.Query["limit"], out limit)) limit = 50;
            if (!request.Query.ContainsKey("offset") || !int.TryParse(request.Query["offset"], out offset)) offset = 0;
            limit = Math.Max(1, Math.Min(200, limit));
            offset = Math.Max(0, offset);
            var conflictPaths = new HashSet<string>(index.Conflicts.OfType<JObject>().Select(item => JsonUtil.GetString(item["path"], "")), StringComparer.OrdinalIgnoreCase);
            Func<JObject, bool> pending = entry => !string.Equals(EntryUtil.HashOf(entry), EntryUtil.BaseHashOf(entry), StringComparison.Ordinal);
            var matches = index.Entries.Values
                .Where(entry => !EntryUtil.Deleted(entry))
                .Where(entry => string.IsNullOrEmpty(query) || EntryUtil.PathOf(entry).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(entry => status == "conflict" ? conflictPaths.Contains(EntryUtil.PathOf(entry))
                    : status == "pending" ? pending(entry) && !conflictPaths.Contains(EntryUtil.PathOf(entry))
                    : status == "synced" ? !pending(entry) && !conflictPaths.Contains(EntryUtil.PathOf(entry)) : true)
                .OrderBy(entry => EntryUtil.PathOf(entry), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            offset = matches.Length == 0 ? 0 : Math.Min(offset, ((matches.Length - 1) / limit) * limit);
            var files = matches.Skip(offset).Take(limit)
                .Select(entry => JsonUtil.Obj(
                    "path", EntryUtil.PathOf(entry),
                    "size", EntryUtil.SizeOf(entry),
                    "mtimeMs", EntryUtil.MtimeOf(entry),
                    "hash", EntryUtil.HashOf(entry),
                    "rev", EntryUtil.RevOf(entry),
                    "deleted", EntryUtil.Deleted(entry),
                    "status", conflictPaths.Contains(EntryUtil.PathOf(entry)) ? "conflict" : pending(entry) ? "pending" : "synced",
                    "conflicted", conflictPaths.Contains(EntryUtil.PathOf(entry))))
                .ToArray();
            return HttpResponse.Json(JsonUtil.Obj("ok", true, "files", new JArray(files), "total", matches.Length, "offset", offset, "limit", limit), 200);
        }

        private HttpResponse BrowseFolder(HttpRequest request, JObject folder)
        {
            var directory = request.Query.ContainsKey("directory") ? request.Query["directory"].Replace('\\', '/').TrimEnd('/') : "";
            try { SafeFiles.Join(JsonUtil.GetString(folder["path"], ""), directory); }
            catch { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "无效的目录路径"), 400); }
            var prefix = directory.Length > 0 ? directory + "/" : "";
            var query = request.Query.ContainsKey("query") ? request.Query["query"].Trim() : "";
            var status = request.Query.ContainsKey("status") ? request.Query["status"] : "all";
            int limit, offset;
            if (!request.Query.ContainsKey("limit") || !int.TryParse(request.Query["limit"], out limit)) limit = 50;
            if (!request.Query.ContainsKey("offset") || !int.TryParse(request.Query["offset"], out offset)) offset = 0;
            limit = Math.Max(1, Math.Min(100, limit)); offset = Math.Max(0, offset);
            var index = Engine.IndexFor(folder);
            if (index.LastScanAt == null) Engine.ScanFolder(folder, false);
            var conflicts = new HashSet<string>(index.Conflicts.OfType<JObject>().Select(item => JsonUtil.GetString(item["path"], "")), StringComparer.OrdinalIgnoreCase);
            var children = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0; int count = 0;
            foreach (var entry in index.Entries.Values)
            {
                var path = EntryUtil.PathOf(entry).Replace('\\', '/');
                if (EntryUtil.Deleted(entry) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length) continue;
                if (query.Length > 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var conflicted = conflicts.Contains(path);
                var pending = !string.Equals(EntryUtil.HashOf(entry), EntryUtil.BaseHashOf(entry), StringComparison.Ordinal);
                if (status == "conflict" && !conflicted || status == "pending" && (!pending || conflicted) || status == "synced" && (pending || conflicted)) continue;
                var remaining = path.Substring(prefix.Length);
                var slash = remaining.IndexOf('/');
                var isDirectory = slash >= 0;
                var name = isDirectory ? remaining.Substring(0, slash) : remaining;
                if (name.Length == 0) continue;
                var childPath = prefix + name;
                JObject child;
                if (!children.TryGetValue(childPath, out child))
                {
                    child = JsonUtil.Obj("name", name, "path", childPath, "type", isDirectory ? "directory" : "file", "size", 0L, "fileCount", 0, "pendingCount", 0, "conflictCount", 0, "mtimeMs", 0d);
                    children[childPath] = child;
                }
                var size = Math.Max(0, EntryUtil.SizeOf(entry));
                child["size"] = JsonUtil.GetLong(child["size"], 0) + size;
                child["fileCount"] = JsonUtil.GetInt(child["fileCount"], 0) + 1;
                child["mtimeMs"] = Math.Max(child["mtimeMs"].Value<double>(), EntryUtil.MtimeOf(entry));
                if (conflicted) child["conflictCount"] = JsonUtil.GetInt(child["conflictCount"], 0) + 1;
                else if (pending) child["pendingCount"] = JsonUtil.GetInt(child["pendingCount"], 0) + 1;
                bytes += size; count++;
            }
            foreach (var child in children.Values) child["status"] = JsonUtil.GetInt(child["conflictCount"], 0) > 0 ? "conflict" : JsonUtil.GetInt(child["pendingCount"], 0) > 0 ? "pending" : "synced";
            var ordered = children.Values.OrderBy(child => child["type"].ToString() == "directory" ? 0 : 1).ThenBy(child => child["name"].ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
            offset = ordered.Length == 0 ? 0 : Math.Min(offset, ((ordered.Length - 1) / limit) * limit);
            var mapItems = children.Values.OrderByDescending(child => bytes > 0 ? JsonUtil.GetLong(child["size"], 0) : JsonUtil.GetInt(child["fileCount"], 0)).ThenBy(child => child["name"].ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
            var chart = new JArray(mapItems.Take(23).Select(child => child.DeepClone()));
            var rest = mapItems.Skip(23).ToArray();
            if (rest.Length > 0) chart.Add(JsonUtil.Obj("name", "其他 " + rest.Length + " 项", "type", "other", "size", rest.Sum(child => JsonUtil.GetLong(child["size"], 0)), "fileCount", rest.Sum(child => JsonUtil.GetInt(child["fileCount"], 0)), "itemCount", rest.Length));
            return HttpResponse.Json(JsonUtil.Obj("ok", true, "directory", directory, "children", new JArray(ordered.Skip(offset).Take(limit).Select(child => child.DeepClone())), "mapItems", chart, "total", ordered.Length, "fileCount", count, "totalBytes", bytes, "directoryCount", ordered.Count(child => child["type"].ToString() == "directory"), "zeroItems", children.Values.Count(child => JsonUtil.GetLong(child["size"], 0) == 0), "offset", offset, "limit", limit), 200);
        }

        private HttpResponse DownloadFile(HttpRequest request, JObject folder)
        {
            var rel = request.Query.ContainsKey("path") ? request.Query["path"].Replace('\\', '/') : "";
            var entry = Engine.IndexFor(folder).Get(rel);
            if (entry == null || EntryUtil.Deleted(entry)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件不存在"), 404);
            var path = SafePath(JsonUtil.GetString(folder["path"], ""), EntryUtil.PathOf(entry));
            if (!File.Exists(path)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "文件不存在"), 404);
            var response = new HttpResponse { FilePath = path, ContentType = "application/octet-stream" };
            response.Headers["content-disposition"] = "attachment; filename=\"" + Path.GetFileName(path).Replace("\"", "") + "\"";
            return response;
        }

        private HttpResponse Reveal(HttpRequest request, JObject folder)
        {
            try
            {
                var text = request.ReadBodyString(1024 * 1024);
                var body = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                var relative = body["path"] != null ? JsonUtil.GetString(body["path"], "") : request.Query.ContainsKey("path") ? request.Query["path"] : "";
                var target = ExplorerIntegration.ExistingPath(SafePath(JsonUtil.GetString(folder["path"], ""), relative));
                revealPath(target);
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse ListVersions(HttpRequest request)
        {
            var folderId = request.Query.ContainsKey("folderId") ? request.Query["folderId"] : "";
            var path = request.Query.ContainsKey("path") ? request.Query["path"] : "";
            return HttpResponse.Json(JsonUtil.Obj("ok", true, "versions", new JArray(Versions.List(folderId, path).Select(item => item.DeepClone()))), 200);
        }

        private HttpResponse VersionContent(string id)
        {
            try
            {
                var data = Versions.Content(id);
                var response = new HttpResponse { Body = data, ContentType = data.Contains((byte)0) ? "application/octet-stream" : "text/plain; charset=utf-8" };
                return response;
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse RestoreVersion(string id)
        {
            try
            {
                var item = Versions.Find(id);
                if (item == null) throw new Exception("历史版本不存在");
                var folder = Config.GetFolder(JsonUtil.GetString(item["folderId"], ""));
                if (folder == null) throw new Exception("文件夹不存在");
                Engine.ChangeConfiguration(delegate { Versions.Restore(id, folder); Engine.ScanFolder(folder, true); });
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse RestoreBatch(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var ids = body["ids"] as JArray;
                var count = 0;
                if (ids != null)
                {
                    foreach (var token in ids)
                    {
                        var response = RestoreVersion(token.ToString());
                        if (response.Status == 200) count += 1;
                    }
                }
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "count", count), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse PeerFolders(HttpRequest request, JObject peer)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var ids = body["folderIds"] as JArray ?? new JArray();
                var notifications = new List<Tuple<JObject, JArray>>();
                Engine.ChangeConfiguration(delegate
                {
                    foreach (var folder in Config.Folders.OfType<JObject>())
                    {
                        var list = folder["peerIds"] as JArray;
                        if (list == null) { list = new JArray(); folder["peerIds"] = list; }
                        var shared = ids.Any(value => value.ToString() == JsonUtil.GetString(folder["id"], ""));
                        var existing = list.Any(value => value.ToString() == JsonUtil.GetString(peer["id"], ""));
                        if (shared == existing) continue;
                        var previous = (JArray)list.DeepClone();
                        if (shared && !existing) list.Add(JsonUtil.GetString(peer["id"], ""));
                        if (!shared && existing)
                        {
                            var kept = list.Where(value => value.ToString() != JsonUtil.GetString(peer["id"], "")).ToArray();
                            folder["peerIds"] = new JArray(kept);
                        }
                        Engine.InvalidateFolder(folder);
                        notifications.Add(Tuple.Create(folder, previous));
                    }
                    Config.Save();
                }, false);
                foreach (var change in notifications) NotifyShares(change.Item1, change.Item2);
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse PeerPatch(HttpRequest request, JObject peer)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                Engine.ChangeConfiguration(delegate
                {
                    var permissionsChanged = body.Properties().Any(property => new[] { "host", "port", "paused" }.Contains(property.Name) && !JToken.DeepEquals(peer[property.Name], property.Value));
                    foreach (var property in body.Properties())
                    {
                        if (property.Name == "name" || property.Name == "host" || property.Name == "port" || property.Name == "autoSync" || property.Name == "paused") peer[property.Name] = property.Value.DeepClone();
                    }
                    Config.Save();
                    if (permissionsChanged) Engine.InvalidatePeer(peer);
                }, false);
                var publicPeer = (JObject)peer.DeepClone(); publicPeer.Remove("token");
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "peer", publicPeer), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse PeerRemove(JObject peer)
        {
            Engine.ChangeConfiguration(delegate
            {
                Engine.InvalidatePeer(peer);
                Config.Peers.Remove(peer);
                foreach (var folder in Config.Folders.OfType<JObject>())
                {
                    var list = folder["peerIds"] as JArray;
                    if (list == null) continue;
                    var kept = list.Where(value => value.ToString() != JsonUtil.GetString(peer["id"], "")).ToArray();
                    folder["peerIds"] = new JArray(kept);
                }
                Config.Save();
            }, false);
            return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
        }

        private HttpResponse ResolveConflict(HttpRequest request, string folderId, string conflictId)
        {
            try
            {
                var folder = Config.GetFolder(folderId);
                if (folder == null) throw new Exception("文件夹不存在");
                var index = Engine.IndexFor(folder);
                var conflict = index.Conflicts.OfType<JObject>().FirstOrDefault(item => JsonUtil.GetString(item["id"], "") == conflictId);
                if (conflict == null) throw new Exception("冲突记录不存在");
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var action = JsonUtil.GetString(body["action"], "keep-current");
                var path = JsonUtil.GetString(conflict["path"], "");
                var conflictPath = JsonUtil.GetString(conflict["conflictPath"], "");
                if (action == "restore-copy" && !string.IsNullOrEmpty(conflictPath))
                {
                    var source = SafePath(JsonUtil.GetString(folder["path"], ""), conflictPath);
                    var target = SafePath(JsonUtil.GetString(folder["path"], ""), path);
                    Versions.Backup(folder, path, "conflict-restore");
                    File.Copy(source, target, true);
                    File.Delete(source);
                    Engine.ScanFolder(folder, true);
                }
                else if (!string.IsNullOrEmpty(conflictPath))
                {
                    var source = SafePath(JsonUtil.GetString(folder["path"], ""), conflictPath);
                    if (File.Exists(source)) File.Delete(source);
                }
                index.Conflicts.Remove(conflict);
                index.Save();
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse RevealGlobal(HttpRequest request)
        {
            try
            {
                var body = JObject.Parse(request.ReadBodyString(1024 * 1024));
                var path = ExplorerIntegration.ExistingPath(JsonUtil.GetString(body["path"], ""));
                revealPath(path);
                return HttpResponse.Json(JsonUtil.Obj("ok", true), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private HttpResponse ListDirectories(HttpRequest request)
        {
            var target = request.Query.ContainsKey("path") ? request.Query["path"] : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathRooted(target)) return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", "路径必须是绝对路径"), 400);
            try
            {
                var parent = Path.GetDirectoryName(target);
                var items = Directory.GetDirectories(target)
                    .Select(path => JsonUtil.Obj("name", Path.GetFileName(path), "path", path))
                    .OrderBy(item => JsonUtil.GetString(item["name"], ""))
                    .Take(500)
                    .ToArray();
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "path", target, "parent", parent, "roots", new JArray("C:\\", "D:\\"), "dirs", new JArray(items)), 200);
            }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 400); }
        }

        private void NewPairingCode()
        {
            var bytes = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var value = BitConverter.ToUInt32(bytes, 0) % 1000000;
            lock (pairingGate)
            {
                pairingCode = value.ToString("D6");
                pairingExpires = DateTime.UtcNow.AddMinutes(10);
                pairingFailures = 0;
                pairingWindow = DateTime.UtcNow;
            }
        }

        private bool ValidatePairCode(string code)
        {
            lock (pairingGate)
            {
                if ((DateTime.UtcNow - pairingWindow).TotalMinutes >= 10) { pairingFailures = 0; pairingWindow = DateTime.UtcNow; }
                if (pairingFailures >= 8) return false;
                if (DateTime.UtcNow > pairingExpires || !DeviceSecurity.Equal(code, pairingCode)) { pairingFailures++; return false; }
                NewPairingCode();
                return true;
            }
        }

        private object PairingState()
        {
            lock (pairingGate)
            {
                var ttl = (int)Math.Max(0, (pairingExpires - DateTime.UtcNow).TotalSeconds);
                return JsonUtil.Obj("code", pairingCode, "ttlSeconds", ttl, "expiresAt", pairingExpires.ToString("o"));
            }
        }

        private object BuildState()
        {
            lock (Engine.ConfigurationGate) return BuildStateSnapshot();
        }

        private object BuildStateSnapshot()
        {
            var peers = new JArray();
            foreach (var peer in Config.Peers.OfType<JObject>())
            {
                var clone = (JObject)peer.DeepClone();
                clone.Remove("token");
                clone["online"] = Engine.IsOnline(JsonUtil.GetString(peer["id"], ""));
                clone["rttMs"] = null;
                clone["initiator"] = string.CompareOrdinal(Config.DeviceId, JsonUtil.GetString(peer["id"], "")) < 0;
                peers.Add(clone);
            }
            var folders = new JArray();
            long totalFiles = 0;
            long totalBytes = 0;
            var totalConflicts = 0;
            var conflicts = new JArray();
            foreach (var folder in Config.Folders.OfType<JObject>())
            {
                var clone = (JObject)folder.DeepClone();
                var index = Engine.IndexFor(folder);
                clone["state"] = JsonUtil.GetString(index.Root["syncState"], "idle");
                clone["fileCount"] = index.FileCount;
                clone["totalBytes"] = index.TotalBytes;
                clone["conflictCount"] = index.ConflictCount;
                clone["lastScanAt"] = index.LastScanAt;
                clone["lastSyncAt"] = index.LastSyncAt;
                clone["watcherActive"] = JsonUtil.GetBool(folder["enabled"], true) && !JsonUtil.GetBool(folder["paused"], false) && Directory.Exists(JsonUtil.GetString(folder["path"], ""));
                clone["lastError"] = index.Root["lastError"];
                clone["pendingCount"] = index.Entries.Values.Count(entry => !string.Equals(EntryUtil.HashOf(entry), EntryUtil.BaseHashOf(entry), StringComparison.Ordinal));
                totalFiles += index.FileCount;
                totalBytes += index.TotalBytes;
                totalConflicts += index.ConflictCount;
                folders.Add(clone);
                foreach (var conflict in index.Conflicts.OfType<JObject>())
                {
                    var item = (JObject)conflict.DeepClone();
                    item["folderId"] = folder["id"];
                    item["folderName"] = folder["name"];
                    conflicts.Add(item);
                }
            }
            var device = DeviceSecurity.PublicDevice(Config.Device);
            device["addresses"] = JArray.FromObject(Config.Addresses());
            var sync = (JObject)Engine.Status.DeepClone();
            return JsonUtil.Obj(
                "now", JsonUtil.NowIso(),
                "device", device,
                "settings", Config.Settings,
                "startupError", StartupError,
                "peers", peers,
                "folders", folders,
                "pendingFolders", Engine.PendingFolders(),
                "security", JsonUtil.Obj("transport", "TLS 1.2", "fingerprint", Security.Fingerprint, "remoteAccessToken", JsonUtil.GetBool(Config.Settings["allowRemoteUi"], false) ? Security.RemoteToken : null),
                "nearby", new JArray(),
                "pairing", PairingState(),
                "sync", sync,
                "activity", Engine.Activity,
                "conflicts", conflicts,
                "versionsCount", Versions.Count,
                "stats", JsonUtil.Obj("totalFiles", totalFiles, "totalBytes", totalBytes, "totalFolders", folders.Count, "onlinePeers", peers.OfType<JObject>().Count(peer => JsonUtil.GetBool(peer["online"], false)), "conflicts", totalConflicts),
                "paths", JsonUtil.Obj("home", Config.Home, "versions", Config.VersionsDir, "logs", Config.LogsDir),
                "server", JsonUtil.Obj("startedAt", StartedAt.ToString("o"), "uptimeSec", (int)(DateTime.UtcNow - StartedAt).TotalSeconds));
        }

        private static string SafePath(string root, string relative)
        {
            return SafeFiles.Join(root, relative);
        }
    }
}
