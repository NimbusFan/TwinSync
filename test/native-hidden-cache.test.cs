using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static void HiddenCacheLiveCase(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "simultaneous-completion"); Put(a, id, "file.bin", "complete payload"); a.Engine.ScanFolder(a.Config.GetFolder(id), true);
            var entry = (JObject)a.Engine.IndexFor(a.Config.GetFolder(id)).Get("file.bin").DeepClone();
            var store = (ResumableTransfers)typeof(CoreService).GetField("transfers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(b);
            var peer = b.Config.GetPeer(a.Config.DeviceId);
            var task = Json(store.Route(peer, TransferRequest("/peer/transfers/start", JsonUtil.Obj("folder", id, "entry", entry))));
            PutBlock(store, peer, task, System.Text.Encoding.UTF8.GetBytes("comp"));
            var part = TransferCache.Upload((string)b.Config.GetFolder(id)["path"], (string)task["id"]);
            var result = Run(b, id);
            var timer = System.Diagnostics.Stopwatch.StartNew(); while (File.Exists(part) && timer.ElapsedMilliseconds < 2000) System.Threading.Thread.Sleep(20);
            Check((string)result["lastResult"] == "success" && File.ReadAllText(FilePath(b, id, "file.bin")) == "complete payload", "The independent pull did not finish the file: " + result.ToString(Newtonsoft.Json.Formatting.None));
            Check(!File.Exists(part) && !File.Exists(Path.Combine(b.Config.Home, "transfers", task["id"] + ".json")), "A successful real sync left its obsolete incoming partial behind");
            Check((File.GetAttributes(FilePath(b, id, "file.bin")) & FileAttributes.Hidden) == 0, "Moving a cache file hid the final user file");
        }
        private static void HiddenCacheCases(string root)
        {
            var config = new ConfigStore(Path.Combine(root, "hidden-cache-home")); var directory = Path.Combine(root, "hidden-cache-files"); Directory.CreateDirectory(directory);
            var peer = JsonUtil.Obj("id", JsonUtil.RandomId(), "token", JsonUtil.RandomToken(32), "fingerprint", new string('A', 64));
            var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "hidden cache", "path", directory, "mode", "two-way", "peerIds", new JArray(peer["id"])); config.Peers.Add(peer); config.Folders.Add(folder); config.Save();
            var engine = new SyncEngine(config, new Dictionary<string, IndexStore>(), new VersionStore(config), code => false, new string('B', 64));
            var store = new ResumableTransfers(config, engine); var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var body = JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "completed-elsewhere.bin", bytes));
            var task = Json(store.Route(peer, TransferRequest("/peer/transfers/start", body))); PutBlock(store, peer, task, bytes.Take(4).ToArray());
            var part = TransferCache.Upload(directory, (string)task["id"]);
            Check(!Directory.GetFiles(directory, ".twinsync-part-*").Any() && (File.GetAttributes(Path.GetDirectoryName(part)) & FileAttributes.Hidden) != 0, "Transfer files still cluttered the visible sync root");
            // Reproduce a simultaneous pull finishing a file while an older incoming upload still has partial bytes.
            File.WriteAllBytes(Path.Combine(directory, "completed-elsewhere.bin"), bytes); engine.ScanFolder(folder, true); store.Maintain(false, true);
            Check(!File.Exists(part) && !File.Exists(Path.Combine(config.Home, "transfers", task["id"] + ".json")), "Completed file left a superseded upload partial behind");
            Check(File.ReadAllBytes(Path.Combine(directory, "completed-elsewhere.bin")).SequenceEqual(bytes), "Completed-file cleanup altered user data");

            var validBody = JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, "valid-partial.bin", bytes));
            task = Json(store.Route(peer, TransferRequest("/peer/transfers/start", validBody))); PutBlock(store, peer, task, bytes.Take(4).ToArray());
            part = TransferCache.Upload(directory, (string)task["id"]); var oldPart = Path.Combine(directory, ".twinsync-part-" + task["id"]); File.Move(part, oldPart);
            store = new ResumableTransfers(config, engine);
            var resumed = Json(store.Route(peer, TransferRequest("/peer/transfers/start", validBody)));
            Check(!File.Exists(oldPart) && File.Exists(part) && (long)resumed["offset"] == 4 && (string)resumed["prefixHash"] == BytesHash(bytes.Take(4).ToArray()), "Legacy partial migration lost verified bytes");
            store.Maintain(false, true); Check(File.Exists(part), "Maintenance removed a valid incomplete transfer");

            var orphanId = new string('e', 64); var orphan = Path.Combine(directory, ".twinsync-part-" + orphanId); File.WriteAllBytes(orphan, bytes.Take(3).ToArray());
            var empty = Path.Combine(directory, ".twinsync-part-" + new string('d', 64)); File.WriteAllText(empty, "");
            var ordinary = Path.Combine(directory, ".twinsync-part-not-an-app-id"); File.WriteAllText(ordinary, "preserve unrelated file");
            store = new ResumableTransfers(config, engine); var preserved = TransferCache.Orphan(directory, orphanId);
            Check(!File.Exists(orphan) && File.ReadAllBytes(preserved).SequenceEqual(bytes.Take(3)), "Unmatched old partial was deleted instead of quarantined");
            Check(!File.Exists(empty) && File.ReadAllText(ordinary) == "preserve unrelated file", "Legacy cleanup failed to distinguish reserved temporary filenames");
            File.SetLastWriteTimeUtc(preserved, DateTime.UtcNow.AddDays(-8)); store.Maintain(false, true);
            Check(!File.Exists(preserved) && File.Exists(part), "Orphan expiry affected a live resume point");

            var index = engine.IndexFor(folder); engine.ScanFolder(folder, true);
            Check(!index.Entries.Keys.Any(TransferCache.Reserved) && !engine.Manifest(folder).Any(v => TransferCache.Reserved((string)v["path"])), "Internal cache was indexed or shared");
            Reject(() => SafeFiles.UserPath(directory, TransferCache.Name + "/owner.txt"), "Internal cache could be read as user content");
            var badBody = JsonUtil.Obj("folder", folder["id"], "entry", CacheEntry(peer, TransferCache.Name + "/owner.txt", bytes));
            Json(store.Route(peer, TransferRequest("/peer/transfers/start", badBody)), 409);
            Check(TransferCache.Owned(Path.Combine(directory, TransferCache.Name)), "Peer upload could overwrite the cache ownership marker");
            var queue = new ScanQueue(); queue.Watching((string)folder["id"], true); queue.Scan(folder, index, config, false);
            queue.Enqueue((string)folder["id"], directory, part); Check(!queue.Scan(folder, index, config, false), "Cache IO triggered another directory scan");

            var destination = Path.Combine(directory, "downloaded.bin"); var key = new string('a', 64); var downloadPart = TransferCache.Download(directory, destination, key, BytesHash(bytes)); File.WriteAllBytes(downloadPart, bytes.Take(4).ToArray());
            File.WriteAllBytes(destination, bytes); engine.ScanFolder(folder, true);
            using (var active = new FileStream(downloadPart, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { TransferCache.Maintain(directory, index); Check(File.Exists(downloadPart), "Maintenance removed an active download"); }
            TransferCache.Maintain(directory, index);
            Check(!File.Exists(downloadPart) && !File.Exists(Path.ChangeExtension(downloadPart, ".json")) && File.ReadAllBytes(destination).SequenceEqual(bytes), "Completed download cache was not reclaimed safely");

            var migration = Path.Combine(root, "hidden-cache-migration"); FolderMigration.CopyAndVerify(directory, migration, index);
            Check(!Directory.Exists(Path.Combine(migration, TransferCache.Name)) && File.ReadAllBytes(Path.Combine(migration, "downloaded.bin")).SequenceEqual(bytes), "Directory migration copied internal cache payloads");
            var collisionRoot = Path.Combine(root, "cache-name-collision"); var collision = Path.Combine(collisionRoot, TransferCache.Name); Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "private.txt"), "keep private contents");
            Reject(() => TransferCache.Ensure(collisionRoot), "An unowned directory was adopted as a cache");
            Check(File.ReadAllText(Path.Combine(collision, "private.txt")) == "keep private contents" && !TransferCache.Owned(collision), "Cache initialization altered an unowned directory");
        }
    }
}
