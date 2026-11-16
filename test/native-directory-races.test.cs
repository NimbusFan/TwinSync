using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static void RemoveRaceDirectory(string path, string root)
        {
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid regression directory.");
            Directory.Delete(target, true);
        }
        private static void ScanRaceCases(string root)
        {
            var config = new ConfigStore(Path.Combine(root, "race-home"));
            var dir = Path.Combine(root, "race-scan"); Directory.CreateDirectory(Path.Combine(dir, "removed"));
            File.WriteAllText(Path.Combine(dir, "removed/a.txt"), "a"); File.WriteAllText(Path.Combine(dir, "removed/b.txt"), "b");
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep");
            var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "path", dir, "ignore", new JArray());
            var index = new IndexStore(config.StateDir, (string)folder["id"]);
            Scanner.Scan(folder, index, config, true);
            var removed = false;
            Scanner.Scan(folder, index, config, true, () => {
                if (!removed) { RemoveRaceDirectory(Path.Combine(dir, "removed"), root); removed = true; }
            });
            Check(EntryUtil.Deleted(index.Get("removed/a.txt")) && EntryUtil.Deleted(index.Get("removed/b.txt")) && !EntryUtil.Deleted(index.Get("keep.txt")), "Directory disappearance aborted scanning or left stale entries.");
            Directory.CreateDirectory(Path.Combine(dir, "during-hash"));
            File.WriteAllBytes(Path.Combine(dir, "during-hash/file.bin"), new byte[131072]);
            Scanner.Scan(folder, index, config, true);
            var checksInHash = 0;
            Scanner.ScanPaths(folder, index, config, new[] { "during-hash" }, () => {
                if (++checksInHash == 2) RemoveRaceDirectory(Path.Combine(dir, "during-hash"), root);
            });
            Check(EntryUtil.Deleted(index.Get("during-hash/file.bin")), "A deleted open snapshot reappeared in the scan index.");
            var queue = new ScanQueue(); queue.Watching((string)folder["id"], true); queue.Scan(folder, index, config, false);
            var busy = Path.Combine(dir, "busy.txt"); File.WriteAllText(busy, "being written");
            queue.Enqueue((string)folder["id"], dir, busy);
            using (var held = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(queue.Scan(folder, index, config, false) && index.Get("busy.txt") == null, "A busy new file caused scan failure or a fabricated entry.");
            Check(queue.Scan(folder, index, config, false) && index.Get("busy.txt") != null, "A busy path was not retried after release.");
            Check(!queue.Scan(folder, index, config, false), "A resolved busy file kept triggering scans.");
            var moved = dir + "-unavailable"; var rootMoved = false;
            Reject(() => Scanner.Scan(folder, index, config, true, () => {
                if (!rootMoved) { Directory.Move(dir, moved); rootMoved = true; }
            }), "An unavailable root was treated as a normal deletion.");
            Directory.Move(moved, dir);
            Check(!EntryUtil.Deleted(index.Get("keep.txt")) && !EntryUtil.Deleted(index.Get("busy.txt")), "Unavailable root generated mass deletions.");
        }
        private static void BrowseRaceCases(CoreService a, string root)
        {
            var id = (string)Api(a, "/api/folders", JsonUtil.Obj("name", "directory changes", "path", Path.Combine(root, "browse-race")), "POST", 201)["folder"]["id"];
            var folder = a.Config.GetFolder(id); var empty = FilePath(a, id, "empty");
            Directory.CreateDirectory(empty);
            var data = Api(a, "/api/folders/" + id + "/browse", null, "GET");
            Check(data["children"].Any(item => (string)item["name"] == "empty" && (string)item["type"] == "directory" && (int)item["fileCount"] == 0), "New empty directory was invisible.");
            Put(a, id, "empty/sub/file.txt", "new nested file"); a.Engine.ScanFolder(folder, true);
            data = Api(a, "/api/folders/" + id + "/browse?directory=empty", null, "GET");
            Check(data["children"].Any(item => (string)item["name"] == "sub"), "New directory content was not browsable.");
            RemoveRaceDirectory(empty, root); a.Engine.ScanFolder(folder, true);
            data = Api(a, "/api/folders/" + id + "/browse?directory=empty/sub&offset=50", null, "GET");
            Check((string)data["directory"] == "" && (int)data["total"] == 0 && (int)data["offset"] == 0, "Deleted browse directory did not return to the existing parent.");
            var failed = (Exception)null;
            var worker = new Thread(() => {
                try { for (var i = 0; i < 30; i++) { Put(a, id, "changing.txt", i.ToString()); a.Engine.ScanFolder(folder, true); } }
                catch (Exception error) { failed = error; }
            });
            worker.Start();
            try { for (var i = 0; i < 30; i++) Api(a, "/api/folders/" + id + "/browse", null, "GET"); }
            finally { worker.Join(5000); }
            Check(failed == null && !worker.IsAlive, "Concurrent browse and scan failed.");
        }
        private static void PauseScanCase(CoreService a, string root)
        {
            var id = (string)Api(a, "/api/folders", JsonUtil.Obj("name", "pause scan", "path", Path.Combine(root, "pause-scan")), "POST", 201)["folder"]["id"];
            Put(a, id, "file.txt", "check cancellation");
            var check = (Action)Delegate.CreateDelegate(typeof(Action), a.Engine, typeof(SyncEngine).GetMethod("CheckScan", BindingFlags.Instance | BindingFlags.NonPublic));
            using (var entered = new ManualResetEvent(false))
            {
                var canceled = false; Exception failed = null;
                var worker = new Thread(() => {
                    try { lock (a.Engine.ConfigurationGate) Scanner.Scan(a.Config.GetFolder(id), a.Engine.IndexFor(a.Config.GetFolder(id)), a.Config, true, () => {
                        entered.Set(); while (!a.Engine.PauseRequested) Thread.Sleep(5); check();
                    }); }
                    catch (OperationCanceledException) { canceled = true; }
                    catch (Exception error) { failed = error; }
                });
                worker.Start(); Check(entered.WaitOne(3000), "Scan did not start.");
                try { Api(a, "/api/settings", JsonUtil.Obj("syncPaused", true), "PATCH"); }
                finally { a.Engine.RequestPause(true); worker.Join(3000); }
                Check(canceled && failed == null && !worker.IsAlive, "Pause waited for the scan lock instead of interrupting hashing.");
                Api(a, "/api/settings", JsonUtil.Obj("syncPaused", false), "PATCH");
                Api(a, "/api/settings", JsonUtil.Obj("syncPaused", "false"), "PATCH", 400);
                Check(!a.Engine.PauseRequested && !(bool)a.Config.Settings["syncPaused"], "Resume or validation left the service paused.");
            }
        }
        private static void PauseNetworkCase(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "pause-network"); Put(a, id, "file.txt", "deliver after resume");
            var peer = a.Config.GetPeer(b.Config.DeviceId); var oldPort = (int)peer["port"]; var heldOnce = 0;
            var route = typeof(CoreService).GetMethod("Route", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var proxy = new HttpServer(b.PublicDir, request => {
                    if (request.Path == "/peer/transfers/start" && Interlocked.Increment(ref heldOnce) == 1) { entered.Set(); release.WaitOne(10000); }
                    return (HttpResponse)route.Invoke(b, new object[] { request });
                }, b.Security.Certificate);
                var port = Port(); proxy.Start(port); peer["port"] = port;
                try
                {
                    var queued = Api(a, "/api/sync", JsonUtil.Obj("folderId", id), "POST", 202);
                    Check(entered.WaitOne(5000), "Network wait did not begin.");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Api(a, "/api/settings", JsonUtil.Obj("syncPaused", true), "PATCH");
                    JObject state = null;
                    while (clock.ElapsedMilliseconds < 2500)
                    {
                        state = Api(a, "/api/state", null, "GET");
                        if (state["sync"]["finishedAt"].Type != JTokenType.Null) break;
                        Thread.Sleep(20);
                    }
                    Check(state != null && (string)state["sync"]["runId"] == (string)queued["runId"] && state["sync"]["finishedAt"].Type != JTokenType.Null && (string)state["sync"]["lastResult"] == "paused", "Pause did not abort blocked network IO promptly.");
                    Check(!File.Exists(FilePath(b, id, "file.txt")) && state["sync"]["error"].Type == JTokenType.Null, "Paused transfer committed a file or reported a failure.");
                    release.Set(); Thread.Sleep(150);
                    Api(a, "/api/settings", JsonUtil.Obj("syncPaused", false), "PATCH");
                    Check((string)Run(a, id)["lastResult"] == "success" && File.ReadAllText(FilePath(b, id, "file.txt")) == "deliver after resume", "Resume did not restore real synchronization.");
                }
                finally { release.Set(); peer["port"] = oldPort; Api(a, "/api/settings", JsonUtil.Obj("syncPaused", false), "PATCH"); proxy.Stop(); }
            }
        }
        private static void TransferDirectoryCase(CoreService a, CoreService b, string root, bool receive, bool pull)
        {
            var name = pull ? (receive ? "delete-pull" : "delete-pull-source") : receive ? "delete-receiver" : "delete-source";
            var id = Share(a, b, root, name);
            var source = pull ? b : a; var receiver = pull ? a : b;
            Put(source, id, "removed/file.bin", new string('x', ResumableTransfers.ChunkSize + 100));
            Put(source, id, "removed/another.txt", "same removed subtree");
            Put(source, id, "keep.txt", "unrelated file");
            Directory.CreateDirectory(FilePath(receiver, id, "removed"));
            source.Engine.ScanFolder(source.Config.GetFolder(id), true);
            var peer = a.Config.GetPeer(b.Config.DeviceId); var oldPort = (int)peer["port"]; var deleted = false; var starts = 0;
            var route = typeof(CoreService).GetMethod("Route", BindingFlags.Instance | BindingFlags.NonPublic);
            var proxy = new HttpServer(b.PublicDir, request => {
                if (request.Path == "/peer/transfers/start") starts++;
                var response = (HttpResponse)route.Invoke(b, new object[] { request });
                var metaPath = Path.Combine(b.Config.Home, "transfers", request.Path.Substring(request.Path.LastIndexOf('/') + 1) + ".json");
                var meta = request.Method == "PUT" ? JsonUtil.LoadObject(metaPath) : null;
                if (!deleted && (pull ? request.Path == "/peer/file" && request.Query["path"].StartsWith("removed/") : request.Method == "PUT" && meta != null && ((string)meta["entry"]["path"]).StartsWith("removed/")))
                {
                    RemoveRaceDirectory(FilePath(receive ? receiver : source, id, "removed"), root); deleted = true;
                }
                return response;
            }, b.Security.Certificate);
            var port = Port(); proxy.Start(port); peer["port"] = port;
            try
            {
                var result = Run(a, id);
                Check(deleted && (string)result["lastResult"] == "success", name + " did not settle after a directory deletion: " + result);
                Check(File.ReadAllText(FilePath(receiver, id, "keep.txt")) == "unrelated file", name + " blocked unrelated files.");
                Check(!File.Exists(FilePath(source, id, "removed/file.bin")) && !File.Exists(FilePath(receiver, id, "removed/file.bin")), name + " resurrected a removed file.");
                var before = starts;
                Check((string)Run(a, id)["lastResult"] == "success" && starts == before, name + " kept retrying nonexistent paths.");
                Check(a.Engine.IndexFor(a.Config.GetFolder(id)).Entries.Values.Where(entry => EntryUtil.PathOf(entry).StartsWith("removed/")).All(EntryUtil.Deleted), name + " retained stale outgoing entries.");
                Put(source, id, "removed/new.txt", "created after deletion");
                source.Engine.ScanFolder(source.Config.GetFolder(id), true);
                Check((string)Run(a, id)["lastResult"] == "success" && File.Exists(FilePath(receiver, id, "removed/new.txt")) && File.ReadAllText(FilePath(receiver, id, "removed/new.txt")) == "created after deletion", name + " blocked a later new file permanently.");
            }
            finally { peer["port"] = oldPort; proxy.Stop(); }
        }
        private static void DirectoryRaceCases(CoreService a, CoreService b, string root)
        {
            var before = checks;
            ScanRaceCases(root); BrowseRaceCases(a, root); PauseScanCase(a, root); PauseNetworkCase(a, b, root);
            TransferDirectoryCase(a, b, root, false, false); TransferDirectoryCase(a, b, root, true, false); TransferDirectoryCase(a, b, root, true, true); TransferDirectoryCase(a, b, root, false, true);
            Console.WriteLine("Directory races and real pause regressions passed: " + (checks - before) + " checks.");
        }
    }
}
