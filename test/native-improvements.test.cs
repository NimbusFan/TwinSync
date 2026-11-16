using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static JObject Json(HttpResponse response, int status = 200)
        {
            if (response.Status != status) throw new Exception("Unexpected status " + response.Status + ": " + Encoding.UTF8.GetString(response.Body));
            return JObject.Parse(Encoding.UTF8.GetString(response.Body));
        }
        private static HttpRequest TransferRequest(string route, JObject body = null, byte[] bytes = null, long? length = null)
        {
            bytes = bytes ?? Encoding.UTF8.GetBytes(body == null ? "{}" : body.ToString());
            return new HttpRequest { Path = route, Method = "POST", Stream = new MemoryStream(bytes), ContentLength = length ?? bytes.Length };
        }
        private static string BytesHash(byte[] bytes) { using (var input = new MemoryStream(bytes)) return AtomicFile.Hash(input); }
        private static void DiscoveryCases()
        {
            var self = JsonUtil.RandomId(); var other = JsonUtil.RandomId();
            var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.Client.LocalEndPoint).Port; socket.Close();
            using (var service = new LanDiscovery(() => JsonUtil.Obj("id", self, "name", "self", "port", 55220), port, false))
            {
                var item = JsonUtil.Obj("service", "TwinSync", "protocol", 2, "id", other, "name", "nearby", "port", 55220, "address", "8.8.8.8", "token", "untrusted");
                var bytes = Encoding.UTF8.GetBytes(item.ToString());
                service.Observe(bytes, IPAddress.Parse("192.168.1.12"), DateTime.UtcNow);
                var found = service.Snapshot();
                Check(found.Count == 1 && (string)found[0]["address"] == "192.168.1.12" && found[0]["token"] == null, "Discovery trusted advertised addresses or credentials");
                service.Observe(bytes, IPAddress.Parse("8.8.8.8"), DateTime.UtcNow);
                Check(service.Snapshot().Count == 1, "Discovery accepted a public source");
                service.Observe(bytes, IPAddress.Parse("192.168.1.12"), DateTime.UtcNow.AddSeconds(-60));
                Check(service.Snapshot().Count == 0, "Stale discovery did not expire");
                item["id"] = self; service.Observe(Encoding.UTF8.GetBytes(item.ToString()), IPAddress.Loopback, DateTime.UtcNow);
                Check(service.Snapshot().Count == 0, "Discovery included itself");
                item["id"] = other; item["port"] = 65536; service.Observe(Encoding.UTF8.GetBytes(item.ToString()), IPAddress.Loopback, DateTime.UtcNow);
                service.Observe(new byte[2048], IPAddress.Loopback, DateTime.UtcNow);
                Check(service.Snapshot().Count == 0, "Discovery accepted malformed announcements");
                item["port"] = 55220; service.SetEnabled(true);
                Check(service.Error == null, "Discovery UDP listener failed");
                using (var sender = new UdpClient()) { bytes = Encoding.UTF8.GetBytes(item.ToString()); sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port)); }
                var wait = System.Diagnostics.Stopwatch.StartNew(); while (service.Snapshot().Count == 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(10);
                Check(service.Snapshot().Count == 1, "Real UDP discovery did not receive a nearby device");
                service.SetEnabled(false); Check(service.Snapshot().Count == 0, "Disabling discovery did not clear nearby devices");
            }
        }
        private static void IncrementalCases(string root)
        {
            var config = new ConfigStore(Path.Combine(root, "scan-home")); var dir = Path.Combine(root, "incremental"); Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "nested"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "old-a"); File.WriteAllText(Path.Combine(dir, "b.txt"), "old-b"); File.WriteAllText(Path.Combine(dir, "nested/c.txt"), "old-c");
            var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "path", dir, "ignore", new JArray()); var index = new IndexStore(config.StateDir, (string)folder["id"]); var queue = new ScanQueue();
            queue.Watching((string)folder["id"], true); Check(queue.Scan(folder, index, config, false), "Initial full scan missing");
            var bh = EntryUtil.HashOf(index.Get("b.txt"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "new-a"); File.WriteAllText(Path.Combine(dir, "b.txt"), "new-b");
            for (var i = 0; i < 100; i++) queue.Enqueue((string)folder["id"], dir, Path.Combine(dir, "a.txt"));
            queue.Scan(folder, index, config, false);
            Check(queue.FullScans == 1 && queue.PathScans == 1 && EntryUtil.HashOf(index.Get("b.txt")) == bh, "Path changes rescanned unrelated files or failed to coalesce");
            Check(EntryUtil.HashOf(index.Get("a.txt")) == JsonUtil.Sha256File(Path.Combine(dir, "a.txt")), "Changed path was not rehashed");
            Check(!queue.Scan(folder, index, config, false), "Idle scan performed repeated IO");
            Directory.Move(Path.Combine(dir, "nested"), Path.Combine(dir, "renamed"));
            queue.Enqueue((string)folder["id"], dir, Path.Combine(dir, "nested")); queue.Enqueue((string)folder["id"], dir, Path.Combine(dir, "renamed")); queue.Scan(folder, index, config, false);
            Check(EntryUtil.Deleted(index.Get("nested/c.txt")) && index.Get("renamed/c.txt") != null && EntryUtil.HashOf(index.Get("b.txt")) == bh, "Directory rename was not scoped correctly");
            queue.Enqueue((string)folder["id"], dir, null); queue.Scan(folder, index, config, false);
            Check(queue.FullScans == 2 && EntryUtil.HashOf(index.Get("b.txt")) != bh, "Overflow recovery did not perform a full scan");
            queue.Watching((string)folder["id"], false);
            var pending = typeof(ScanQueue).GetField("pending", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(queue) as System.Collections.IDictionary;
            var value = pending[(string)folder["id"]]; value.GetType().GetField("LastFull", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, DateTime.UtcNow.AddMinutes(-11));
            Check(queue.Scan(folder, index, config, false) && queue.FullScans == 3, "Periodic full reconciliation was removed");
            queue.Enqueue((string)folder["id"], dir, Path.Combine(dir, ".twinsync-part-" + new string('a', 64)));
            Check(!queue.Scan(folder, index, config, false), "Internal partial file triggered a scan");
        }
        private static void PreviewCases(CoreService a, string root)
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            {
                var text = string.Concat(Enumerable.Repeat("中文\U0001F642\r\n", 18000)); var data = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                using (var input = new MemoryStream(data))
                {
                    long offset = 0; var result = new StringBuilder(); var pages = 0;
                    while (true) { var page = TextPreview.Read(input, offset, "fixture", "fixture"); Check(!(bool)page["binary"] && ((string)page["text"]).Length <= 32768, "Text preview exceeded its bound or broke Unicode"); result.Append((string)page["text"]); pages++; if ((bool)page["eof"]) break; Check((long)page["nextOffset"] > offset, "Text paging did not advance"); offset = (long)page["nextOffset"]; }
                    Check(result.ToString() == text && pages > 1, "Paged text did not round-trip exactly");
                    Reject(() => TextPreview.Read(input, 0, "changed", "fixture"), "Changed preview was accepted");
                }
            }
            using (var input = new MemoryStream(new byte[] { 0, 1, 2, 3 })) Check((bool)TextPreview.Read(input, 0, null, "binary")["binary"], "Binary preview was decoded as text");
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 90000) + "\U0001F642")))
            { var page = TextPreview.Read(input, 0, null, "line"); Check(((string)page["text"]).Length == 32768 && !(bool)page["eof"], "Single long line was not bounded"); }
            var id = Api(a, "/api/folders", JsonUtil.Obj("name", "preview", "path", Path.Combine(root, "preview-root")), "POST", 201)["folder"]["id"].ToString();
            Put(a, id, "text.txt", new string('a', 100000)); var saved = a.Versions.Backup(a.Config.GetFolder(id), "text.txt", "preview");
            var preview = Api(a, "/api/versions/" + saved["id"] + "/preview", null, "GET");
            Check(((string)preview["text"]).Length <= 32768 && !(bool)preview["eof"], "HTTP version preview read the whole file");
            var current = Api(a, "/api/folders/" + id + "/preview?path=text.txt", null, "GET"); Put(a, id, "text.txt", "edited");
            Api(a, "/api/folders/" + id + "/preview?path=text.txt&offset=" + current["nextOffset"] + "&stamp=" + Uri.EscapeDataString((string)current["stamp"]), null, "GET", 409);
            Check(true, "Changing current files must invalidate paging");
        }
        private static void JournalCases(CoreService a, string root)
        {
            var config = new ConfigStore(Path.Combine(root, "log-home")); config.Device["name"] = "private-device";
            config.Peers.Add(JsonUtil.Obj("name", "private-peer", "token", "very-private-token"));
            var journal = new ActivityJournal(config, 16384);
            for (var i = 0; i < 220; i++) journal.Append("error", "fixture " + i, "very-private-token 192.168.1.2 2001:db8::1 C:\\Users\\secret\\file.txt " + new string('x', 900), "warn");
            Check(Directory.GetFiles(config.LogsDir).Length <= 4 && Directory.GetFiles(config.LogsDir).All(p => new FileInfo(p).Length <= 16384), "Persistent logs exceeded rotation capacity");
            var restored = new ActivityJournal(config, 16384); var events = restored.Read(300);
            Check(events.Count > 0 && (string)events[0]["title"] == "fixture 219", "Journal did not survive restart in newest-first order");
            DateTime at; Check(DateTime.TryParse((string)events[0]["at"], out at), "Journal redaction corrupted timestamps");
            var all = string.Join("", Directory.GetFiles(config.LogsDir).Select(File.ReadAllText));
            Check(!all.Contains("very-private-token") && !all.Contains("192.168.1.2") && !all.Contains("2001:db8::1") && !all.Contains("secret\\\\file"), "Sensitive data was persisted in logs");
            journal.Append("device", "connected", "private-peer", "success");
            var report = journal.Export(JsonUtil.Obj("state", "idle", "currentFile", "private-file.txt")).ToString();
            Check(!report.Contains("private-peer") && !report.Contains("private-device") && !report.Contains("private-file.txt") && !report.Contains("very-private-token"), "Diagnostics leaked private names or state fields");
            var locked = Path.Combine(root, "log-locked"); var failing = new ConfigStore(locked); Directory.Delete(failing.LogsDir); File.WriteAllText(failing.LogsDir, "blocked");
            var broken = new ActivityJournal(failing); broken.Append("sync", "nonfatal", "", "success"); Check(broken.Error != null && broken.Read(1).Count == 1, "A log IO failure broke the service or disappeared");
            var apiReport = Api(a, "/api/diagnostics/export", null, "GET"); Check(apiReport["activity"] is JArray && apiReport["config"] == null && apiReport["device"] == null, "Diagnostics endpoint exported raw configuration");
        }
        private static void BatchCases(CoreService a, string root)
        {
            var id = Api(a, "/api/folders", JsonUtil.Obj("name", "batch", "path", Path.Combine(root, "batch-root")), "POST", 201)["folder"]["id"].ToString();
            Put(a, id, "first.txt", "original-one"); Put(a, id, "second.txt", "original-two");
            var folder = a.Config.GetFolder(id); var first = a.Versions.Backup(folder, "first.txt", "fixture"); var second = a.Versions.Backup(folder, "second.txt", "fixture");
            Put(a, id, "first.txt", "edited-one"); File.Delete(FilePath(a, id, "second.txt")); Directory.CreateDirectory(FilePath(a, id, "second.txt"));
            var result = Api(a, "/api/versions/restore-batch", JsonUtil.Obj("ids", new JArray(first["id"], second["id"])));
            Check((int)result["count"] == 1 && (int)result["failed"] == 1 && result["results"].Count() == 2 && (bool)result["results"][0]["success"] && !string.IsNullOrEmpty((string)result["results"][1]["error"]), "Batch restore did not return individual outcomes");
            Check(File.ReadAllText(FilePath(a, id, "first.txt")) == "original-one", "Successful batch entry was not restored");
            Directory.Delete(FilePath(a, id, "second.txt")); Put(a, id, "first.txt", "keep-after-success");
            result = Api(a, "/api/versions/restore-batch", JsonUtil.Obj("ids", new JArray(result["results"].Where(v => !(bool)v["success"]).Select(v => v["id"].DeepClone()))));
            Check((int)result["count"] == 1 && File.ReadAllText(FilePath(a, id, "first.txt")) == "keep-after-success" && File.ReadAllText(FilePath(a, id, "second.txt")) == "original-two", "Retry of failed entries changed a successful item");
            result = Api(a, "/api/versions/restore-batch", JsonUtil.Obj("ids", new JArray(JsonUtil.RandomId())));
            Check((int)result["count"] == 0 && (int)result["failed"] == 1 && result["results"][0]["error"] != null, "Missing versions were silently counted as successful");
            a.Config.Settings["maxVersionsPerFile"] = 2;
            Put(a, id, "same.txt", "oldest"); var older = a.Versions.Backup(folder, "same.txt", "fixture");
            Put(a, id, "same.txt", "newer"); var newer = a.Versions.Backup(folder, "same.txt", "fixture"); Put(a, id, "same.txt", "current");
            result = Api(a, "/api/versions/restore-batch", JsonUtil.Obj("ids", new JArray(newer["id"], older["id"])));
            Check((int)result["count"] == 2 && File.ReadAllText(FilePath(a, id, "same.txt")) == "oldest", "Retention cleanup deleted a selected version during batch restore");
            a.Config.Settings["maxVersionsPerFile"] = 10;
        }
        private static void ResumeCases(CoreService a, CoreService b, string root, string app)
        {
            var id = Share(a, b, root, "resume"); var folder = b.Config.GetFolder(id); var peer = b.Config.GetPeer(a.Config.DeviceId); var store = new ResumableTransfers(b.Config, b.Engine);
            var data = new byte[6 * 1024 * 1024 + 17]; for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            var entry = JsonUtil.Obj("path", "resume.bin", "hash", BytesHash(data), "size", data.Length, "mtimeMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "rev", 900000, "origin", a.Config.DeviceId);
            var body = JsonUtil.Obj("folder", id, "entry", entry); var start = Json(store.Route(peer, TransferRequest("/peer/transfers/start", body))); var key = (string)start["id"];
            var first = data.Take(ResumableTransfers.ChunkSize).ToArray();
            var chunk = TransferRequest("/peer/transfers/" + key, null, first); chunk.Method = "PUT"; chunk.Query["offset"] = "0"; chunk.Query["hash"] = BytesHash(first);
            Check((long)Json(store.Route(peer, chunk))["offset"] == first.Length && !File.Exists(FilePath(b, id, "resume.bin")), "Partial upload was committed prematurely");
            var tail = data.Skip(first.Length).ToArray();
            chunk = TransferRequest("/peer/transfers/" + key, null, tail.Take(100000).ToArray(), tail.Length); chunk.Method = "PUT"; chunk.Query["offset"] = first.Length.ToString(); chunk.Query["hash"] = BytesHash(tail);
            Json(store.Route(peer, chunk), 409);
            var again = Json(store.Route(peer, TransferRequest("/peer/transfers/start", body)));
            Check((long)again["offset"] == first.Length && (string)again["prefixHash"] == BytesHash(first), "Interrupted chunk did not roll back to the verified checkpoint");
            var part = TransferCache.Upload((string)folder["path"], key); using (var extra = new FileStream(part, FileMode.Append)) extra.WriteByte(42);
            b.Config.Save(); var reloaded = new CoreService(app, b.Config.Home, b.Port, null, enabled => { }); var recovered = new ResumableTransfers(reloaded.Config, reloaded.Engine); var reloadedPeer = reloaded.Config.GetPeer(a.Config.DeviceId);
            again = Json(recovered.Route(reloadedPeer, TransferRequest("/peer/transfers/start", body)));
            Check((long)again["offset"] == first.Length && new FileInfo(part).Length == first.Length, "Restart did not restore the checkpoint or trim an uncommitted tail");
            chunk = TransferRequest("/peer/transfers/" + key, null, tail); chunk.Method = "PUT"; chunk.Query["offset"] = first.Length.ToString(); chunk.Query["hash"] = new string('0', 64);
            Json(recovered.Route(reloadedPeer, chunk), 409); Check(new FileInfo(part).Length == first.Length, "Invalid block checksum advanced the checkpoint");
            chunk.Stream.Position = 0; chunk.Query["hash"] = BytesHash(tail); Json(recovered.Route(reloadedPeer, chunk));
            Json(recovered.Route(reloadedPeer, TransferRequest("/peer/transfers/" + key + "/commit")));
            Check(JsonUtil.Sha256File(FilePath(b, id, "resume.bin")) == EntryUtil.HashOf(entry) && !File.Exists(part), "Resumed upload did not commit the verified complete file");
            Check((bool)Json(recovered.Route(reloadedPeer, TransferRequest("/peer/transfers/start", body)))["complete"], "Retry after a lost commit response was not idempotent");
            b.Engine.ScanFolder(folder, true);

            entry = (JObject)entry.DeepClone(); entry["path"] = "revoked.bin"; body["entry"] = entry;
            start = Json(store.Route(peer, TransferRequest("/peer/transfers/start", body))); key = (string)start["id"];
            Api(b, "/api/folders/" + id, JsonUtil.Obj("paused", true), "PATCH"); Api(b, "/api/folders/" + id, JsonUtil.Obj("paused", false), "PATCH");
            chunk = TransferRequest("/peer/transfers/" + key, null, first); chunk.Method = "PUT"; chunk.Query["offset"] = "0"; chunk.Query["hash"] = BytesHash(first);
            Json(store.Route(peer, chunk), 403); Check(!File.Exists(FilePath(b, id, "revoked.bin")), "Regrant revived a revoked resumable session");
            var afterRestart = new CoreService(app, b.Config.Home, b.Port, null, enabled => { });
            Check(new ResumableTransfers(afterRestart.Config, afterRestart.Engine).Route(afterRestart.Config.GetPeer(a.Config.DeviceId), chunk).Status == 404, "Revoked cache entry survived restart");
            Check(true, "Authorization revocation must survive process restart");

            File.WriteAllBytes(FilePath(a, id, "network.bin"), data); a.Engine.ScanFolder(a.Config.GetFolder(id), true); var networkEntry = a.Engine.IndexFor(a.Config.GetFolder(id)).Get("network.bin");
            var faultPort = Port(); var routeMethod = typeof(CoreService).GetMethod("Route", BindingFlags.NonPublic | BindingFlags.Instance); var chunks = 0; var starts = 0;
            var offsets = new System.Collections.Generic.List<long>();
            var proxy = new HttpServer(b.PublicDir, request => { if (request.Path == "/peer/transfers/start") starts++; if (request.Method == "PUT" && request.Path.StartsWith("/peer/transfers/") && Interlocked.Increment(ref chunks) == 2) return HttpResponse.Json(JsonUtil.Obj("error", "temporary unavailable"), 503); var response = (HttpResponse)routeMethod.Invoke(b, new object[] { request }); if (request.Path == "/peer/transfers/start") lock (offsets) offsets.Add(JsonUtil.GetLong(Json(response)["offset"], -1)); return response; }, b.Security.Certificate);
            var outgoing = (JObject)a.Config.GetPeer(b.Config.DeviceId).DeepClone(); outgoing["port"] = faultPort; proxy.Start(faultPort);
            try { new PeerClient(outgoing, a.Config.DeviceId).Upload(id, networkEntry, FilePath(a, id, "network.bin"), false); }
            finally { proxy.Stop(); }
            Check(starts >= 2 && offsets.Contains(ResumableTransfers.ChunkSize) && JsonUtil.Sha256File(FilePath(b, id, "network.bin")) == EntryUtil.HashOf(networkEntry), "Transient upload failure did not retry and resume over TLS at the verified offset");
            var remote = b.Engine.IndexFor(folder).Get("network.bin"); var destination = Path.Combine(root, "resume-download.bin");
            Reject(() => new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Download(id, remote, destination, (done, total) => { if (done >= ResumableTransfers.ChunkSize) throw new OperationCanceledException("test interruption"); }), "Download interruption was ignored");
            var downloads = Directory.GetFiles(Path.Combine(root, TransferCache.Name), "download-*.part"); Check(downloads.Length == 1 && new FileInfo(downloads[0]).Length == ResumableTransfers.ChunkSize, "Verified download block was not retained");
            long resumedAt = -1;
            var downloaded = new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Download(id, remote, destination, (done, total) => { if (resumedAt < 0) resumedAt = done; });
            Check(resumedAt == ResumableTransfers.ChunkSize && JsonUtil.Sha256File((string)downloaded["temp"]) == EntryUtil.HashOf(remote), "Ranged download failed to resume at its saved offset");
            File.Delete((string)downloaded["temp"]);
            Reject(() => new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Download(id, remote, destination, (done, total) => { if (done >= ResumableTransfers.ChunkSize) throw new OperationCanceledException("test interruption"); }), "Second download interruption was ignored");
            downloads = Directory.GetFiles(Path.Combine(root, TransferCache.Name), "download-*.part"); using (var corrupt = new FileStream(downloads[0], FileMode.Open, FileAccess.Write)) corrupt.WriteByte(255);
            var reset = false;
            downloaded = new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Download(id, remote, destination, (done, total) => { if (done == 0) reset = true; });
            Check(reset && JsonUtil.Sha256File((string)downloaded["temp"]) == EntryUtil.HashOf(remote), "Corrupt download prefix was reused or could not recover safely");
            File.Delete((string)downloaded["temp"]);

            Put(b, id, "guarded.txt", "base"); b.Engine.ScanFolder(folder, true);
            entry = JsonUtil.Obj("path", "guarded.txt", "hash", BytesHash(first), "size", first.Length, "mtimeMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "rev", 990000, "origin", a.Config.DeviceId);
            start = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", id, "entry", entry)))); key = (string)start["id"];
            chunk = TransferRequest("/peer/transfers/" + key, null, first); chunk.Method = "PUT"; chunk.Query["offset"] = "0"; chunk.Query["hash"] = BytesHash(first); Json(store.Route(peer, chunk));
            Put(b, id, "guarded.txt", "edited during upload"); Json(store.Route(peer, TransferRequest("/peer/transfers/" + key + "/commit")), 409);
            Check(File.ReadAllText(FilePath(b, id, "guarded.txt")) == "edited during upload", "Resumed commit overwrote an unscanned local edit");
        }
        private static void Improvements(CoreService a, CoreService b, string root, string app)
        {
            var before = checks; DiscoveryCases(); IncrementalCases(root); PreviewCases(a, root); JournalCases(a, root); BatchCases(a, root); ResumeCases(a, b, root, app); TransferCacheCases(root); FolderContractCases(a, root); SourceSnapshotCases(a, b, root); ResumeProtocolCases(a, b, root); RemovedInvitationCase(a, b, root); HiddenCacheCases(root); HiddenCacheLiveCase(a, b, root);
            Console.WriteLine("Discovery, incremental scans, bounded previews, journal, resume and batch regressions passed: " + (checks - before) + " checks.");
        }
    }
}
