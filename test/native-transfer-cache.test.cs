using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static JObject CacheEntry(JObject peer, string name, byte[] data)
        {
            return JsonUtil.Obj("path", name, "hash", BytesHash(data), "size", data.Length, "mtimeMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "rev", 900000, "origin", peer["id"]);
        }
        private static void PutBlock(ResumableTransfers store, JObject peer, JObject start, byte[] data)
        {
            var request = TransferRequest("/peer/transfers/" + start["id"], null, data); request.Method = "PUT";
            request.Query["offset"] = start["offset"].ToString(); request.Query["hash"] = BytesHash(data);
            Json(store.Route(peer, request));
        }
        private static void TransferCacheCases(string root)
        {
            var config = new ConfigStore(Path.Combine(root, "cache-home")); var dir = Path.Combine(root, "cache-files"); Directory.CreateDirectory(dir);
            var peer = JsonUtil.Obj("id", JsonUtil.RandomId(), "token", JsonUtil.RandomToken(32), "fingerprint", new string('A', 64));
            var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "cache", "path", dir, "mode", "two-way", "peerIds", new JArray(peer["id"]));
            config.Peers.Add(peer); config.Folders.Add(folder); config.Save();
            var engine = new SyncEngine(config, new Dictionary<string, IndexStore>(), new VersionStore(config), code => false, new string('B', 64));
            var store = new ResumableTransfers(config, engine); var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var sessions = Path.Combine(config.Home, "transfers");
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "original user data");
            // Reproduce old installations: sixteen empty attempts kept occupying every admission slot.
            for (var i = 0; i < 16; i++)
            {
                var start = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "empty-" + i, bytes)))));
                var metaPath = Path.Combine(sessions, start["id"] + ".json"); var meta = JsonUtil.LoadObject(metaPath); meta["updatedAt"] = DateTime.UtcNow.AddMinutes(-3).ToString("o"); JsonUtil.SaveAtomic(metaPath, meta);
            }
            Check(Directory.GetFiles(sessions, "*.json").Length == 16, "Failed to reproduce the sixteen-task cache limit");
            var admitted = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "healthy.bin", bytes)))));
            PutBlock(store, peer, admitted, bytes); Json(store.Route(peer, TransferRequest("/peer/transfers/" + admitted["id"] + "/commit")));
            Check(File.ReadAllBytes(Path.Combine(dir, "healthy.bin")).SequenceEqual(bytes) && Directory.GetFiles(sessions, "*.json").Length == 0, "Stale attempts blocked a healthy transfer");
            Check(File.ReadAllText(Path.Combine(dir, "keep.txt")) == "original user data", "Cache recovery touched a user file");
            // A terminal write failure used to remove the part but leak its metadata, eventually blocking all later files.
            for (var i = 0; i < 24; i++)
            {
                var name = "blocked-" + i; Directory.CreateDirectory(Path.Combine(dir, name));
                var start = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, name, bytes)))));
                PutBlock(store, peer, start, bytes); Json(store.Route(peer, TransferRequest("/peer/transfers/" + start["id"] + "/commit")), 409);
                Check(!File.Exists(Path.Combine(sessions, start["id"] + ".json")) && Directory.Exists(Path.Combine(dir, name)), "Terminal failure leaked a task or changed its destination directory");
            }
            var healthy = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "after-errors.bin", bytes)))));
            PutBlock(store, peer, healthy, bytes); Json(store.Route(peer, TransferRequest("/peer/transfers/" + healthy["id"] + "/commit")));
            Check(File.Exists(Path.Combine(dir, "after-errors.bin")), "Twenty-four failures poisoned subsequent transfers");
            var retained = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "resume-valid.bin", bytes)))));
            PutBlock(store, peer, retained, bytes.Take(4).ToArray());
            var missing = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "missing.bin", bytes)))));
            File.Delete(TransferCache.Upload(dir, (string)missing["id"]));
            store = new ResumableTransfers(config, engine);
            var resumed = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "resume-valid.bin", bytes)))));
            Check((long)resumed["offset"] == 4 && (string)resumed["prefixHash"] == BytesHash(bytes.Take(4).ToArray()), "Startup discarded a valid verified partial");
            Check(!File.Exists(Path.Combine(sessions, missing["id"] + ".json")), "Startup retained metadata without its part file");
        }
        private static void FolderContractCases(CoreService a, string root)
        {
            var dir = Path.Combine(root, "duplicate-folder");
            var added = Api(a, "/api/folders", JsonUtil.Obj("name", "one mapping", "path", dir), "POST", 201);
            var id = (string)added["folder"]["id"]; Put(a, id, "keep.txt", "keep me"); var count = a.Config.Folders.Count;
            Api(a, "/api/folders", JsonUtil.Obj("name", "duplicate", "path", dir + "\\", "id", id), "POST", 400);
            Check(a.Config.Folders.Count == count && File.ReadAllText(Path.Combine(dir, "keep.txt")) == "keep me", "Duplicate directory binding changed configuration or files");
            Api(a, "/api/folders/" + id, JsonUtil.Obj("name", "renamed alias"), "PATCH");
            Check((string)a.Config.GetFolder(id)["name"] == "renamed alias", "Duplicate guard rejected editing the existing mapping");
            a.Engine.AddActivity("file", "contract event", "persisted history", "info");
            var activity = Api(a, "/api/activity?limit=300", null, "GET");
            Check(activity["activity"].Any(item => (string)item["title"] == "contract event"), "Real history endpoint lost activity records");
        }
        private static void SourceSnapshotCases(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "snapshot"); Put(a, id, "file.txt", "outgoing snapshot"); a.Engine.ScanFolder(a.Config.GetFolder(id), true);
            var entry = (JObject)a.Engine.IndexFor(a.Config.GetFolder(id)).Get("file.txt").DeepClone();
            var replacement = Path.Combine(root, "snapshot-replacement"); File.WriteAllText(replacement, "new local content"); var replaced = false;
            new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Upload(id, entry, FilePath(a, id, "file.txt"), false, (done, total) => {
                if (!replaced) { AtomicFile.CopyVerified(replacement, FilePath(a, id, "file.txt"), JsonUtil.Sha256File(replacement), EntryUtil.HashOf(entry), 0, null); replaced = true; }
            });
            Check(replaced && File.ReadAllText(FilePath(a, id, "file.txt")) == "new local content" && File.ReadAllText(FilePath(b, id, "file.txt")) == "outgoing snapshot", "Opening an outgoing snapshot blocked atomic replacement or mixed its bytes");
            var blocked = FilePath(a, id, "locked.txt"); File.WriteAllText(blocked, "locked"); a.Engine.ScanFolder(a.Config.GetFolder(id), true);
            var before = Directory.GetFiles(Path.Combine(b.Config.Home, "transfers"), "*.json").Length;
            using (var held = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Upload(id, a.Engine.IndexFor(a.Config.GetFolder(id)).Get("locked.txt"), blocked, false), "Locked source unexpectedly uploaded");
            Check(Directory.GetFiles(Path.Combine(b.Config.Home, "transfers"), "*.json").Length == before, "Unreadable source allocated an empty receiver task");
        }
        private static void ResumeProtocolCases(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "resume-protocol");
            var handler = typeof(CoreService).GetMethod("Route", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var oldServer in new[] { true, false })
            {
                var name = oldServer ? "legacy.txt" : "retry.txt"; Put(a, id, name, "protocol fixture"); a.Engine.ScanFolder(a.Config.GetFolder(id), true);
                var entry = (JObject)a.Engine.IndexFor(a.Config.GetFolder(id)).Get(name).DeepClone();
                var starts = 0; var blocks = 0; var legacy = 0; var port = Port();
                var proxy = new HttpServer(b.PublicDir, request => {
                    if (request.Path == "/peer/transfers/start") { starts++; if (oldServer) return HttpResponse.Json(JsonUtil.Obj("error", "unsupported"), 404); }
                    if (request.Method == "PUT" && request.Path.StartsWith("/peer/transfers/") && ++blocks == 1 && !oldServer) return HttpResponse.Json(JsonUtil.Obj("error", "expired cache"), 404);
                    if (request.Method == "PUT" && request.Path == "/peer/file") legacy++;
                    return (HttpResponse)handler.Invoke(b, new object[] { request });
                }, b.Security.Certificate);
                proxy.Start(port); var peer = (JObject)a.Config.GetPeer(b.Config.DeviceId).DeepClone(); peer["port"] = port;
                try { new PeerClient(peer, a.Config.DeviceId).Upload(id, entry, FilePath(a, id, name), false); }
                finally { proxy.Stop(); }
                Check(File.ReadAllText(FilePath(b, id, name)) == "protocol fixture", "Protocol recovery lost file content");
                Check(oldServer ? legacy == 1 : legacy == 0 && starts >= 2, "Cache expiry incorrectly fell back to legacy upload or broke old-server compatibility");
            }
        }
        private static void RemovedInvitationCase(CoreService a, CoreService b, string root)
        {
            var folderPath = Path.Combine(root, "withdrawn-offer");
            var id = (string)Api(a, "/api/folders", JsonUtil.Obj("name", "withdrawn", "path", folderPath, "peerIds", new JArray(b.Config.DeviceId)), "POST", 201)["folder"]["id"];
            var timer = System.Diagnostics.Stopwatch.StartNew(); while (!b.Engine.PendingFolders().Any(v => (string)v["id"] == id) && timer.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(20);
            Check(b.Engine.PendingFolders().Any(v => (string)v["id"] == id), "Fixture invitation did not reach receiver");
            File.WriteAllText(Path.Combine(folderPath, "keep.txt"), "preserve original"); Api(a, "/api/folders/" + id, null, "DELETE");
            timer.Restart(); while (b.Engine.PendingFolders().Any(v => (string)v["id"] == id) && timer.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(20);
            Check(!b.Engine.PendingFolders().Any(v => (string)v["id"] == id) && File.ReadAllText(Path.Combine(folderPath, "keep.txt")) == "preserve original", "Removing a task left a stale invitation or deleted local data");
        }
    }
}
