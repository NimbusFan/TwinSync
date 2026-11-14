using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static void Reject(Action action, string message)
        {
            var failed = false; try { action(); } catch { failed = true; } Check(failed, message);
        }

        private static void AtomicCases(string root)
        {
            var target = Path.Combine(root, "atomic 中文.txt"); var source = Path.Combine(root, "replacement.txt");
            File.WriteAllText(target, "original"); File.WriteAllText(source, "replacement");
            var old = JsonUtil.Sha256File(target); var hash = JsonUtil.Sha256File(source);
            Reject(() => AtomicFile.CopyVerified(source, target, new string('0', 64), old, 0, null), "Bad payload hash was accepted");
            Check(File.ReadAllText(target) == "original", "Bad payload destroyed original bytes");
            var edited = false;
            Reject(() => AtomicFile.CopyVerified(source, target, hash, old, 0, delegate { if (!edited) { File.WriteAllText(target, "edited during transfer"); edited = true; } }), "Concurrent local edit was overwritten");
            Check(File.ReadAllText(target) == "edited during transfer", "Latest local edit was lost");
            old = JsonUtil.Sha256File(target);
            using (var busy = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => AtomicFile.CopyVerified(source, target, hash, old, 0, null), "Locked target was replaced");
            Check(File.ReadAllText(target) == "edited during transfer", "Failed replacement removed old file");
            AtomicFile.CopyVerified(source, target, hash, old, 0, null);
            Check(File.ReadAllText(target) == "replacement" && !Directory.GetFiles(root, "*.twinsync-part-*").Any(), "Atomic replacement or temporary cleanup failed");
            Reject(() => AtomicFile.DeleteUnchanged(target, old, null), "Changed file was deleted");
            var metadata = Path.Combine(root, "atomic-index.json"); JsonUtil.SaveAtomic(metadata, JsonUtil.Obj("value", "old"));
            using (var busy = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => JsonUtil.SaveAtomic(metadata, JsonUtil.Obj("value", "new")), "Locked metadata unexpectedly saved");
            Check(JsonUtil.LoadObject(metadata)["value"].ToString() == "old", "Metadata failure deleted old index");
        }

        private static void VersionCases(string root)
        {
            var config = new ConfigStore(Path.Combine(root, "version-fixture")); config.Settings["maxVersionsPerFile"] = 1;
            var files = Path.Combine(root, "version-files"); Directory.CreateDirectory(files);
            var one = JsonUtil.Obj("id", Guid.NewGuid().ToString(), "name", "one", "path", files);
            var two = JsonUtil.Obj("id", Guid.NewGuid().ToString(), "name", "two", "path", files);
            var store = new VersionStore(config); var path = Path.Combine(files, "file.txt");
            File.WriteAllText(path, "shared history"); var first = store.Backup(one, "file.txt", "test"); store.Backup(two, "file.txt", "test");
            var shared = Path.Combine(config.VersionsDir, "blobs", first["hash"].ToString().Substring(0, 2), first["hash"].ToString());
            File.WriteAllText(path, "next history"); store.Backup(one, "file.txt", "test");
            Check(File.Exists(shared) && store.Content(store.List(two["id"].ToString(), null)[0]["id"].ToString()).Length > 0, "Cleanup deleted a blob still used by another folder");
            store.Backup(two, "file.txt", "test"); Check(!File.Exists(shared), "Unreferenced old blob was not reclaimed");
            var restorePath = Path.Combine(files, "restore-only.txt"); File.WriteAllText(restorePath, "unique selected history");
            var chosen = store.Backup(one, "restore-only.txt", "test"); File.WriteAllText(restorePath, "current before restore");
            store.Restore(chosen["id"].ToString(), one);
            Check(File.ReadAllText(restorePath) == "unique selected history", "Restore failed when backup pruned its selected version at limit one");
            Check(System.Text.Encoding.UTF8.GetString(store.Content(store.List(one["id"].ToString(), "restore-only.txt")[0]["id"].ToString())) == "current before restore", "Restore did not preserve current contents");
            var index = Path.Combine(config.VersionsDir, "index.json"); var before = File.ReadAllText(index); var count = store.Count;
            File.WriteAllText(path, "orphan after failed save");
            using (var busy = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => store.Backup(one, "file.txt", "test"), "Version metadata failure was hidden");
            Check(File.ReadAllText(index) == before && store.Count == count, "Failed history save changed references");
            store.Maintain(true);
            var corrupt = store.Backup(one, "file.txt", "test"); var corruptHash = corrupt["hash"].ToString();
            File.WriteAllText(Path.Combine(config.VersionsDir, "blobs", corruptHash.Substring(0, 2), corruptHash), "damaged backup");
            count = store.Count;
            Reject(() => store.Backup(one, "file.txt", "test"), "Corrupt existing history blob was accepted as a safe backup");
            Check(store.Count == count && File.ReadAllText(path) == "orphan after failed save", "Corrupt history check changed current contents or metadata");
            var expired = store.Items;
            foreach (JObject item in expired) item["savedAt"] = DateTime.UtcNow.AddDays(-4000).ToString("o");
            JsonUtil.SaveAtomic(index, expired); store = new VersionStore(config); store.Maintain(true);
            Check(store.Count == 0 && !Directory.GetFiles(Path.Combine(config.VersionsDir, "blobs"), "*", SearchOption.AllDirectories).Any(), "Scheduled expiry left records or unreferenced blobs");
        }

        private static void ConflictCases(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "newest-policy"); Put(a, id, "file.txt", "base"); Run(a, id);
            foreach (var core in new[] { a, b }) Api(core, "/api/settings", JsonUtil.Obj("conflictPolicy", "newest"), "PATCH");
            Put(a, id, "file.txt", "older edit with higher revision"); Put(b, id, "file.txt", "newer edit with lower revision");
            File.SetLastWriteTimeUtc(FilePath(a, id, "file.txt"), DateTime.UtcNow.AddMinutes(-2)); File.SetLastWriteTimeUtc(FilePath(b, id, "file.txt"), DateTime.UtcNow);
            a.Engine.ScanFolder(a.Config.GetFolder(id), true); b.Engine.ScanFolder(b.Config.GetFolder(id), true);
            var local = a.Engine.IndexFor(a.Config.GetFolder(id)).Get("file.txt"); var remote = b.Engine.IndexFor(b.Config.GetFolder(id)).Get("file.txt");
            local["rev"] = 9000; remote["rev"] = 2;
            Check(EntryUtil.Decide(local, remote, "two-way", "newest") == "pull" && EntryUtil.Decide(local, remote, "two-way", "copy") == "push", "Conflict policy did not affect selection");
            var result = Run(a, id);
            Check(result["lastResult"].ToString() == "success" && File.ReadAllText(FilePath(a, id, "file.txt")) == "newer edit with lower revision", "Newest policy ignored modification time");
            Check(a.Engine.IndexFor(a.Config.GetFolder(id)).ConflictCount == 0 && !Directory.GetFiles(a.Config.GetFolder(id)["path"].ToString()).Any(file => file.Contains("冲突")), "Newest policy created conflict copies");
            Check(a.Versions.List(id, "file.txt").Any(), "Newest policy skipped history backup");
            foreach (var core in new[] { a, b }) Api(core, "/api/settings", JsonUtil.Obj("conflictPolicy", "copy"), "PATCH");
            var copy = Share(a, b, root, "copy-policy"); Put(a, copy, "file.txt", "base"); Run(a, copy);
            Put(a, copy, "file.txt", "local edit"); Put(b, copy, "file.txt", "remote edit");
            a.Engine.ScanFolder(a.Config.GetFolder(copy), true); b.Engine.ScanFolder(b.Config.GetFolder(copy), true);
            b.Engine.IndexFor(b.Config.GetFolder(copy)).Get("file.txt")["rev"] = 10000;
            Run(a, copy);
            Check(a.Engine.IndexFor(a.Config.GetFolder(copy)).ConflictCount > 0 && Directory.GetFiles(a.Config.GetFolder(copy)["path"].ToString()).Any(file => file.Contains("冲突")), "Copy policy stopped preserving conflict copies");
            Api(a, "/api/settings", JsonUtil.Obj("conflictPolicy", "invalid"), "PATCH", 400);
        }

        private sealed class MidTransferStream : MemoryStream
        {
            private readonly Action action; private int reads;
            internal MidTransferStream(byte[] data, Action action) : base(data) { this.action = action; }
            public override int Read(byte[] buffer, int offset, int count) { var read = base.Read(buffer, offset, count); if (++reads == 2) action(); return read; }
        }

        private static void InterruptedUpload(CoreService receiver, JObject peer, string id, Action change, string name)
        {
            var data = new byte[256 * 1024]; for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            string hash; using (var stream = new MemoryStream(data)) hash = AtomicFile.Hash(stream);
            var folder = receiver.Config.GetFolder(id); var directory = folder["path"].ToString();
            using (var stream = new MidTransferStream(data, change))
            {
                var request = new HttpRequest { Stream = stream, ContentLength = data.Length };
                request.Query["folder"] = id; request.Query["path"] = name; request.Query["hash"] = hash; request.Query["rev"] = "100000";
                var method = typeof(CoreService).GetMethod("PeerUpload", BindingFlags.Instance | BindingFlags.NonPublic);
                var response = (HttpResponse)method.Invoke(receiver, new object[] { peer, request });
                Check(response.Status != 200 && !File.Exists(Path.Combine(directory, name)), "In-flight upload committed after " + name);
            }
            Check(!Directory.GetFiles(directory, ".twinsync-part-*").Any(), "Revoked upload leaked partial files: " + name);
        }

        private static void AuthorizationCases(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "authorization"); var folder = b.Config.GetFolder(id); var peer = b.Config.GetPeer(a.Config.DeviceId);
            var guard = b.Engine.TransferGuard(peer, folder, true);
            InterruptedUpload(b, peer, id, () => Api(b, "/api/peers/" + a.Config.DeviceId + "/folders", JsonUtil.Obj("folderIds", new JArray(b.Config.Folders.OfType<JObject>().Where(item => item["id"].ToString() != id).Select(item => item["id"].DeepClone())))), "unshared.bin");
            Api(b, "/api/folders/" + id, JsonUtil.Obj("peerIds", new JArray(a.Config.DeviceId)), "PATCH");
            Reject(guard, "Re-sharing revived a previously authorized transfer");
            InterruptedUpload(b, peer, id, () => Api(b, "/api/folders/" + id, JsonUtil.Obj("paused", true), "PATCH"), "folder-pause.bin");
            Api(b, "/api/folders/" + id, JsonUtil.Obj("paused", false), "PATCH");
            InterruptedUpload(b, peer, id, () => Api(b, "/api/peers/" + a.Config.DeviceId, JsonUtil.Obj("paused", true), "PATCH"), "paused.bin");
            Api(b, "/api/peers/" + a.Config.DeviceId, JsonUtil.Obj("paused", false), "PATCH");
            InterruptedUpload(b, peer, id, () => Api(b, "/api/settings", JsonUtil.Obj("syncPaused", true), "PATCH"), "global-pause.bin");
            Api(b, "/api/settings", JsonUtil.Obj("syncPaused", false), "PATCH");
            Put(b, id, "edited.txt", "before"); b.Engine.ScanFolder(folder, true);
            var upload = Path.Combine(root, "new-upload.tmp"); File.WriteAllText(upload, "remote");
            var incoming = JsonUtil.Obj("path", "edited.txt", "hash", JsonUtil.Sha256File(upload), "size", 6, "mtimeMs", 0, "rev", 200000, "origin", a.Config.DeviceId, "deleted", false);
            var check = b.Engine.TransferGuard(peer, folder, true, "edited.txt"); Put(b, id, "edited.txt", "saved while uploading");
            Reject(() => b.Engine.AcceptIncomingFile(peer, folder, "edited.txt", incoming, upload, check), "Upload overwrote a local edit made after scanning");
            Check(File.ReadAllText(FilePath(b, id, "edited.txt")) == "saved while uploading" && !File.Exists(upload), "Local edit or incoming temporary cleanup failed");
            InterruptedUpload(b, peer, id, () => Api(b, "/api/folders/" + id, null, "DELETE"), "removed-folder.bin");
            id = Share(a, b, root, "removed-peer");
            InterruptedUpload(b, peer, id, () => Api(b, "/api/peers/" + a.Config.DeviceId, null, "DELETE"), "removed-peer.bin");
        }

        private static void ProgressCases(CoreService a, CoreService b, string root)
        {
            var id = Share(a, b, root, "progress"); var file = FilePath(a, id, "large.bin"); var buffer = new byte[65536];
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
            using (var output = File.Create(file)) for (var block = 0; block < 1024; block++) output.Write(buffer, 0, buffer.Length);
            var queued = Api(a, "/api/sync", JsonUtil.Obj("folderId", id), "POST", 202); var timer = System.Diagnostics.Stopwatch.StartNew(); var sawProgress = false; JObject sync = null;
            while (timer.ElapsedMilliseconds < 20000)
            {
                sync = (JObject)Api(a, "/api/state", null, "GET")["sync"];
                if (JsonUtil.GetBool(sync["transferActive"], false) && JsonUtil.GetLong(sync["fileBytesDone"], 0) > 0 && JsonUtil.GetLong(sync["fileBytesTotal"], 0) == new FileInfo(file).Length && JsonUtil.GetLong(sync["speed"], 0) > 0) sawProgress = true;
                if (sync["runId"].ToString() == queued["runId"].ToString() && sync["finishedAt"].Type != JTokenType.Null) break;
                Thread.Sleep(5);
            }
            Check(sawProgress, "Active transfer did not report byte progress and speed");
            Check(sync["lastResult"].ToString() == "success" && !(bool)sync["transferActive"] && (long)sync["speed"] == 0 && JsonUtil.Sha256File(file) == JsonUtil.Sha256File(FilePath(b, id, "large.bin")), "Progress run failed or left stale transfer status");
            var entry = b.Engine.IndexFor(b.Config.GetFolder(id)).Get("large.bin"); long done = -1; var events = 0;
            var destination = Path.Combine(root, "download-progress.bin");
            var downloaded = new PeerClient(a.Config.GetPeer(b.Config.DeviceId), a.Config.DeviceId).Download(id, entry, destination, (bytes, total) => { if (bytes < done || total != new FileInfo(file).Length) throw new Exception("Invalid download progress"); done = bytes; events++; });
            Check(events >= 2 && done == new FileInfo(file).Length, "Download progress did not reach the verified payload size");
            File.Delete(downloaded["temp"].ToString());
        }

        private static void Reliability(CoreService a, CoreService b, string root)
        {
            for (var attempt = 0; attempt < 20; attempt++) Api(a, "/api/pairing/refresh", JsonUtil.Obj("ignored", new string('x', 32000)));
            Check(true, "Small unread POST bodies must not reset successful responses");
            AtomicCases(root); VersionCases(root); ConflictCases(a, b, root); ProgressCases(a, b, root); AuthorizationCases(a, b, root);
        }
    }
}
