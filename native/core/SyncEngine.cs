using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class SyncEngine
    {
        public ConfigStore Config { get; private set; }
        public Dictionary<string, IndexStore> Indexes { get; private set; }
        public VersionStore Versions { get; private set; }
        public JObject Status { get; private set; }
        public JArray Activity { get; private set; }
        public JArray Conflicts { get; private set; }
        private readonly Func<string, bool> validatePairCode;
        private readonly Dictionary<string, bool> online = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private readonly Dictionary<string, long> permissions = new Dictionary<string, long>();
        private long pauseGeneration;
        private bool syncing;
        private readonly string fingerprint;
        internal object ConfigurationGate { get { return gate; } }
        public void ChangeConfiguration(Action action, bool requireIdle = true)
        {
            lock (gate) { if (requireIdle && syncing) throw new Exception("正在同步，请等待本次同步结束后再修改目录设置。"); action(); }
        }

        private long Generation(string key) { long value; return permissions.TryGetValue(key, out value) ? value : 0; }
        internal void InvalidateFolder(JObject folder) { lock (gate) { var key = "folder:" + folder["id"]; permissions[key] = Generation(key) + 1; } }
        internal void InvalidatePeer(JObject peer) { lock (gate) { var key = "peer:" + peer["id"]; permissions[key] = Generation(key) + 1; } }
        internal void InvalidatePause() { lock (gate) pauseGeneration++; }

        internal Action TransferGuard(JObject peer, JObject folder, bool writing = false, string relativePath = null)
        {
            lock (gate)
            {
                var peerKey = "peer:" + peer["id"]; var folderKey = "folder:" + folder["id"];
                var peerGeneration = Generation(peerKey); var folderGeneration = Generation(folderKey); var pausedAt = pauseGeneration;
                var path = folder["path"].ToString(); var mode = JsonUtil.GetString(folder["mode"], "two-way");
                var token = JsonUtil.GetString(peer["token"], "");
                var before = relativePath == null ? null : IndexFor(folder).Get(relativePath);
                var beforeHash = EntryUtil.HashOf(before); var beforeDeleted = EntryUtil.Deleted(before);
                Action check = delegate
                {
                    lock (gate)
                    {
                        if (!object.ReferenceEquals(Config.GetPeer(peer["id"].ToString()), peer) || !object.ReferenceEquals(Config.GetFolder(folder["id"].ToString()), folder)
                            || Generation(peerKey) != peerGeneration || Generation(folderKey) != folderGeneration || pauseGeneration != pausedAt
                            || JsonUtil.GetBool(Config.Settings["syncPaused"], false) || JsonUtil.GetBool(peer["paused"], false)
                            || !JsonUtil.GetBool(folder["enabled"], true) || JsonUtil.GetBool(folder["paused"], false)
                            || folder["path"].ToString() != path || JsonUtil.GetString(folder["mode"], "two-way") != mode || JsonUtil.GetString(peer["token"], "") != token
                            || !((folder["peerIds"] as JArray) ?? new JArray()).Any(id => id.ToString() == peer["id"].ToString()) || (writing && mode == "send-only"))
                            throw new OperationCanceledException("同步授权或设置已变更，本次任务已停止，请按当前范围重新同步。");
                        if (relativePath != null)
                        {
                            var current = IndexFor(folder).Get(relativePath);
                            if (EntryUtil.HashOf(current) != beforeHash || EntryUtil.Deleted(current) != beforeDeleted) throw new IOException("本地文件在传输期间发生变化，已保留，请重新核对。");
                        }
                    }
                };
                return check;
            }
        }

        private Action<long, long> TransferProgress(string direction)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            return delegate(long done, long total)
            {
                lock (gate)
                {
                    Status["transferActive"] = true; Status["transferDirection"] = direction;
                    Status["fileBytesDone"] = done; Status["fileBytesTotal"] = total;
                    Status["speed"] = done == 0 ? 0 : (long)(done / Math.Max(0.001, clock.Elapsed.TotalSeconds));
                }
            };
        }

        public SyncEngine(ConfigStore config, Dictionary<string, IndexStore> indexes, VersionStore versions, Func<string, bool> validatePairCode, string fingerprint)
        {
            Config = config;
            Indexes = indexes;
            Versions = versions;
            this.validatePairCode = validatePairCode;
            this.fingerprint = fingerprint;
            Status = JsonUtil.Obj("state", "idle", "message", "同步服务已就绪", "currentFile", null, "currentFolder", null, "currentPeer", null, "speed", 0, "total", 0, "done", 0, "lastSyncAt", null, "error", null);
            Activity = new JArray();
            Conflicts = new JArray();
        }

        public IndexStore IndexFor(JObject folder)
        {
            lock (gate)
            {
                var id = JsonUtil.GetString(folder["id"], "");
                IndexStore index;
                if (!Indexes.TryGetValue(id, out index))
                {
                    index = new IndexStore(Config.StateDir, id);
                    Indexes[id] = index;
                }
                return index;
            }
        }

        public bool IsOnline(string peerId)
        {
            lock (gate) { bool value; return online.TryGetValue(peerId, out value) && value; }
        }

        public void ScanFolder(JObject folder, bool force)
        {
            lock (gate)
            {
                if (!JsonUtil.GetBool(folder["enabled"], true) || JsonUtil.GetBool(folder["paused"], false)) return;
                Scanner.Scan(folder, IndexFor(folder), Config, force);
            }
        }

        private sealed class SyncPair { internal JObject Peer, Folder; internal Action Check; }
        private sealed class PairResult { internal int Failures, Conflicts, Completed; }

        public void SyncAll(bool manual) { SyncSelected(manual, null, null, false); }
        public JObject QueueSync(bool manual, string folderId, string peerId) { return SyncSelected(manual, folderId, peerId, true); }

        private JObject SyncSelected(bool manual, string folderId, string peerId, bool queue)
        {
            List<SyncPair> pairs;
            string runId;
            lock (gate)
            {
                if (syncing) return JsonUtil.Obj("accepted", false, "reason", "busy", "message", "同步正在进行中");
                if (!manual && !JsonUtil.GetBool(Config.Settings["autoSync"], true)) return JsonUtil.Obj("accepted", false, "reason", "automatic-disabled");
                if (!string.IsNullOrEmpty(folderId) && Config.GetFolder(folderId) == null) throw new Exception("选中的同步文件夹不存在。");
                if (!string.IsNullOrEmpty(peerId) && Config.GetPeer(peerId) == null) throw new Exception("选中的设备不存在。");
                pairs = (from folder in Config.Folders.OfType<JObject>()
                         where (string.IsNullOrEmpty(folderId) || folder["id"].ToString() == folderId) && JsonUtil.GetBool(folder["enabled"], true) && !JsonUtil.GetBool(folder["paused"], false)
                         from peer in Config.Peers.OfType<JObject>()
                         where (string.IsNullOrEmpty(peerId) || peer["id"].ToString() == peerId) && !JsonUtil.GetBool(peer["paused"], false) && (manual || JsonUtil.GetBool(peer["autoSync"], true))
                         where ((folder["peerIds"] as JArray) ?? new JArray()).Any(id => id.ToString() == peer["id"].ToString())
                         select new SyncPair { Peer = peer, Folder = folder, Check = TransferGuard(peer, folder) }).ToList();
                Status["scope"] = JsonUtil.Obj("folderId", folderId, "peerId", peerId);
                Status["lastAttemptAt"] = JsonUtil.NowIso();
                if (JsonUtil.GetBool(Config.Settings["syncPaused"], false) || pairs.Count == 0)
                {
                    var reason = JsonUtil.GetBool(Config.Settings["syncPaused"], false) ? "同步已暂停" : "没有可执行的同步任务，请检查文件夹、设备共享范围和暂停状态。";
                    SetStatus("waiting", reason, null); Status["lastResult"] = "skipped"; Status["error"] = null; Status["errors"] = new JArray();
                    Status["summary"] = JsonUtil.Obj("succeeded", 0, "failed", 0, "conflicts", 0, "skipped", 1);
                    return JsonUtil.Obj("accepted", false, "reason", "skipped", "message", reason);
                }
                syncing = true;
                runId = JsonUtil.RandomId(); Status["runId"] = runId; Status["lastResult"] = null; Status["finishedAt"] = null;
                Status["error"] = null; Status["errors"] = new JArray(); Status["done"] = 0; Status["total"] = pairs.Count;
                SetStatus("syncing", "正在检查设备", null);
            }
            if (queue) ThreadPool.QueueUserWorkItem(delegate { RunPairs(pairs); });
            else RunPairs(pairs);
            return JsonUtil.Obj("accepted", true, "runId", runId, "pairs", pairs.Count, "message", "已开始同步所选内容");
        }

        private void RunPairs(List<SyncPair> pairs)
        {
            var errors = new JArray(); var successes = 0; var failed = 0; var conflicts = 0; var skipped = 0; var completedFiles = 0;
            var folderFailures = new HashSet<string>();
            var pingResults = new Dictionary<string, string>();
            try
            {
                foreach (var pair in pairs)
                {
                    var peer = pair.Peer; var folder = pair.Folder;
                    var folderId = folder["id"].ToString(); var peerId = peer["id"].ToString();
                    try
                    {
                        pair.Check();
                        lock (gate) { Status["currentFolder"] = folderId; Status["currentPeer"] = peerId; IndexFor(folder).Root["syncState"] = "syncing"; }
                        if (!pingResults.ContainsKey(peerId))
                        {
                            try { new PeerClient(peer, Config.DeviceId).Ping(); pingResults[peerId] = null; lock (gate) online[peerId] = true; }
                            catch (Exception error) { pingResults[peerId] = error.Message; lock (gate) online[peerId] = false; }
                        }
                        if (pingResults[peerId] != null) throw new Exception(pingResults[peerId]);
                        ScanFolder(folder, false);
                        var result = Reconcile(peer, folder, errors, pair.Check);
                        pair.Check();
                        completedFiles += result.Completed;
                        if (result.Failures > 0 || result.Conflicts > 0) { folderFailures.Add(folderId); failed += result.Failures > 0 ? 1 : 0; conflicts += result.Conflicts; }
                        else successes++;
                    }
                    catch (OperationCanceledException) { skipped++; folderFailures.Add(folderId); }
                    catch (Exception error)
                    {
                        failed++; folderFailures.Add(folderId);
                        RecordSyncError(errors, folder, peer, null, error.Message);
                    }
                    finally { lock (gate) Status["done"] = JsonUtil.GetInt(Status["done"], 0) + 1; }
                }
                var outcome = failed > 0 ? (successes > 0 || completedFiles > 0 ? "partial" : "failed") : conflicts > 0 ? "conflict" : skipped > 0 ? "skipped" : "success";
                var message = outcome == "success" ? "所选内容同步完成" : outcome == "partial" ? "部分同步失败，请查看失败项目" : outcome == "failed" ? "同步失败，请检查设备连接和文件权限" : outcome == "conflict" ? "存在文件冲突，需要确认" : "同步未全部执行，请检查暂停状态";
                lock (gate)
                {
                    foreach (var folder in pairs.Select(pair => pair.Folder).Distinct())
                    {
                        var index = IndexFor(folder); var id = folder["id"].ToString();
                        if (!object.ReferenceEquals(Config.GetFolder(id), folder)) continue;
                        var previousSuccess = index.LastSyncAt;
                        index.Root["syncState"] = folderFailures.Contains(id) ? (conflicts > 0 && failed == 0 ? "conflict" : skipped > 0 && failed == 0 ? "waiting" : "error") : "synced";
                        index.Root["lastError"] = folderFailures.Contains(id) ? message : null;
                        index.Root["lastResult"] = folderFailures.Contains(id) ? outcome : "success";
                        if (!folderFailures.Contains(id)) index.LastSyncAt = JsonUtil.NowIso();
                        try { index.Save(); }
                        catch { index.LastSyncAt = previousSuccess; index.Root["syncState"] = "error"; throw; }
                    }
                    Status["lastResult"] = outcome;
                    Status["errors"] = errors;
                    Status["summary"] = JsonUtil.Obj("succeeded", successes, "failed", failed, "conflicts", conflicts, "skipped", skipped);
                    Status["error"] = errors.Count > 0 ? errors[0]["message"] : null;
                    if (outcome == "success") Status["lastSyncAt"] = JsonUtil.NowIso();
                    SetStatus(outcome == "failed" ? "error" : outcome == "partial" ? "partial" : outcome == "conflict" ? "conflict" : outcome == "skipped" ? "waiting" : "idle", message, null);
                    AddActivity("sync", message, errors.Count > 0 ? errors[0]["message"].ToString() : "", outcome == "success" ? "success" : "warn");
                }
            }
            catch (Exception error)
            {
                lock (gate) { Status["lastResult"] = "failed"; Status["error"] = error.Message; SetStatus("error", "同步失败：" + error.Message, null); }
            }
            finally
            {
                lock (gate) { syncing = false; Status["transferActive"] = false; Status["speed"] = 0; Status["finishedAt"] = JsonUtil.NowIso(); Status["currentFolder"] = null; Status["currentPeer"] = null; }
            }
        }

        private void RecordSyncError(JArray errors, JObject folder, JObject peer, string path, string message)
        {
            if (errors.Count < 30) errors.Add(JsonUtil.Obj("folderId", folder["id"], "peerId", peer["id"], "path", path, "message", message));
        }

        private PairResult Reconcile(JObject peer, JObject folder, JArray errors, Action check)
        {
            var result = new PairResult();
            var index = IndexFor(folder);
            var client = new PeerClient(peer, Config.DeviceId, check);
            var manifest = client.Manifest(JsonUtil.GetString(folder["id"], ""));
            var remote = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var remoteArray = manifest["entries"] as JArray;
            if (remoteArray == null) throw new Exception("对方返回了无效的文件清单。");
            if (remoteArray != null)
            {
                foreach (var token in remoteArray.OfType<JObject>())
                {
                    var path = EntryUtil.PathOf(token).Replace('\\', '/');
                    if (!string.IsNullOrEmpty(path)) remote[path] = token;
                }
            }
            HashSet<string> union;
            lock (gate) union = new HashSet<string>(index.Entries.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var path in remote.Keys) union.Add(path);
            var mode = JsonUtil.GetString(folder["mode"], "two-way");
            foreach (var path in union)
            {
                check();
                JObject local;
                lock (gate) { Status["currentFile"] = path; Status["transferActive"] = false; Status["speed"] = 0; local = index.Get(path); if (local != null) local = (JObject)local.DeepClone(); }
                var remoteEntry = remote.ContainsKey(path) ? remote[path] : null;
                var action = EntryUtil.Decide(local, remoteEntry, mode, JsonUtil.GetString(Config.Settings["conflictPolicy"], "copy"));
                try
                {
                    if (action == "pull") Pull(peer, folder, index, remoteEntry, local, check);
                    else if (action == "push") Push(peer, folder, index, local, remoteEntry, check);
                    else if (action == "same" && local != null && remoteEntry != null)
                    {
                        lock (gate)
                        {
                            check();
                            if (EntryUtil.HashOf(index.Get(path)) != EntryUtil.HashOf(local)) throw new IOException("文件索引已变化，请重新核对。");
                            local["baseHash"] = EntryUtil.HashOf(remoteEntry);
                            local["rev"] = Math.Max(EntryUtil.RevOf(local), EntryUtil.RevOf(remoteEntry));
                            index.Upsert(local);
                        }
                    }
                    result.Completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    result.Failures++;
                    RecordSyncError(errors, folder, peer, path, error.Message);
                }
            }
            lock (gate) { check(); result.Conflicts = index.ConflictCount; index.Save(); }
            return result;
        }

        private void Pull(JObject peer, JObject folder, IndexStore index, JObject remote, JObject local, Action check)
        {
            var target = SafePath(JsonUtil.GetString(folder["path"], ""), EntryUtil.PathOf(remote));
            if (EntryUtil.Deleted(remote))
            {
                lock (gate)
                {
                    check();
                    if (local != null && !EntryUtil.Deleted(local))
                    {
                        Versions.Backup(folder, EntryUtil.PathOf(local), "peer-delete");
                        AtomicFile.DeleteUnchanged(target, EntryUtil.HashOf(local), check);
                        local["deleted"] = true; local["hash"] = null; local["size"] = 0;
                        local["rev"] = EntryUtil.RevOf(remote); local["origin"] = EntryUtil.OriginOf(remote);
                        index.Upsert(local);
                    }
                }
                return;
            }
            var downloaded = new PeerClient(peer, Config.DeviceId, check).Download(JsonUtil.GetString(folder["id"], ""), remote, target, TransferProgress("download"));
            var temp = JsonUtil.GetString(downloaded["temp"], "");
            try
            {
                lock (gate)
                {
                    check();
                    var expected = local == null || EntryUtil.Deleted(local) ? null : EntryUtil.HashOf(local);
                    AtomicFile.AssertUnchanged(target, expected);
                    if (EntryUtil.BothChanged(local, remote) && JsonUtil.GetString(Config.Settings["conflictPolicy"], "copy") == "copy") PreserveConflict(folder, index, local, peer, "both-changed");
                    Versions.Backup(folder, EntryUtil.PathOf(remote), "peer-overwrite");
                    AtomicFile.Commit(temp, target, EntryUtil.HashOf(remote), expected, EntryUtil.MtimeOf(remote), delegate { check(); SafePath(folder["path"].ToString(), EntryUtil.PathOf(remote)); });
                    var entry = (JObject)remote.DeepClone(); entry["baseHash"] = EntryUtil.HashOf(remote); entry["updatedAt"] = JsonUtil.NowIso();
                    index.ObserveRev(EntryUtil.RevOf(entry)); index.Upsert(entry);
                }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private void Push(JObject peer, JObject folder, IndexStore index, JObject local, JObject remote, Action check)
        {
            var client = new PeerClient(peer, Config.DeviceId, check);
            if (EntryUtil.Deleted(local))
            {
                AtomicFile.AssertUnchanged(SafePath(folder["path"].ToString(), EntryUtil.PathOf(local)), null);
                if (remote != null && !EntryUtil.Deleted(remote))
                {
                    var deletion = client.Delete(JsonUtil.GetString(folder["id"], ""), EntryUtil.PathOf(local), local);
                    if (JsonUtil.GetBool(deletion["conflict"], false)) throw new Exception("对方文件有修改，未执行删除，请处理冲突。");
                }
                return;
            }
            var source = SafePath(JsonUtil.GetString(folder["path"], ""), EntryUtil.PathOf(local));
            if (!File.Exists(source)) throw new FileNotFoundException("待发送的文件已不存在，请重新扫描。", source);
            var response = client.Upload(JsonUtil.GetString(folder["id"], ""), local, source, false, TransferProgress("upload"));
            if (JsonUtil.GetBool(response["conflict"], false)) throw new Exception("对方保留了不同版本，请先处理文件冲突。");
            if (JsonUtil.GetBool(response["superseded"], false)) throw new Exception("对方文件较新，未覆盖；请重新核对以接收新版本。");
            lock (gate)
            {
                check();
                if (EntryUtil.HashOf(index.Get(EntryUtil.PathOf(local))) != EntryUtil.HashOf(local)) throw new IOException("本地文件索引已变化，请重新核对。");
                local["baseHash"] = EntryUtil.HashOf(local); index.Upsert(local);
            }
        }

        private void PreserveConflict(JObject folder, IndexStore index, JObject local, JObject peer, string kind)
        {
            var existing = new HashSet<string>(index.Entries.Keys.Select(path => path.ToLowerInvariant()));
            var conflictPath = EntryUtil.ConflictPath(EntryUtil.PathOf(local), Config.DeviceName, existing);
            var source = SafePath(JsonUtil.GetString(folder["path"], ""), EntryUtil.PathOf(local));
            var target = SafePath(JsonUtil.GetString(folder["path"], ""), conflictPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            AtomicFile.CopyVerified(source, target, EntryUtil.HashOf(local), null, EntryUtil.MtimeOf(local), null);
            var conflict = JsonUtil.Obj(
                "id", JsonUtil.RandomId(),
                "path", EntryUtil.PathOf(local),
                "conflictPath", conflictPath,
                "kind", kind,
                "peerId", JsonUtil.GetString(peer["id"], ""),
                "peerName", JsonUtil.GetString(peer["name"], ""),
                "localHash", EntryUtil.HashOf(local),
                "remoteHash", "",
                "message", "双方同时修改，已保留本机副本",
                "createdAt", JsonUtil.NowIso());
            index.AddConflict(conflict);
            Conflicts.Add(conflict.DeepClone());
        }

        public JObject AcceptIncomingFile(JObject peer, JObject folder, string relPath, JObject incoming, string temp, Action transferCheck = null)
        {
            try
            {
                lock (gate)
                {
                    var check = transferCheck ?? TransferGuard(peer, folder, true); check();
                    return AcceptIncomingFileCore(peer, folder, relPath, incoming, temp, check);
                }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private JObject AcceptIncomingFileCore(JObject peer, JObject folder, string relPath, JObject incoming, string temp, Action check)
        {
            var index = IndexFor(folder);
            var local = index.Get(relPath);
            var mode = JsonUtil.GetString(folder["mode"], "two-way");
            if (mode == "send-only")
            {
                File.Delete(temp);
                throw new Exception("该文件夹设置为仅发送");
            }
            var hash = EntryUtil.HashOf(incoming);
            var destination = SafePath(folder["path"].ToString(), relPath);
            var expected = local == null || EntryUtil.Deleted(local) ? null : EntryUtil.HashOf(local);
            AtomicFile.AssertUnchanged(destination, expected);
            if (local == null || EntryUtil.Deleted(local))
            {
                var target = SafePath(JsonUtil.GetString(folder["path"], ""), relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                AtomicFile.Commit(temp, target, hash, null, EntryUtil.MtimeOf(incoming), check);
                incoming["baseHash"] = hash;
                incoming["updatedAt"] = JsonUtil.NowIso();
                index.Upsert(incoming);
                index.ObserveRev(EntryUtil.RevOf(incoming));
                index.Save();
                return JsonUtil.Obj("ok", true, "entry", incoming);
            }
            if (EntryUtil.HashOf(local) == hash)
            {
                File.Delete(temp);
                local["baseHash"] = hash;
                local["rev"] = Math.Max(EntryUtil.RevOf(local), EntryUtil.RevOf(incoming));
                index.Upsert(local);
                index.Save();
                return JsonUtil.Obj("ok", true, "entry", local);
            }
            var policy = JsonUtil.GetString(Config.Settings["conflictPolicy"], "copy");
            if (EntryUtil.CompareForSync(incoming, local, policy) <= 0)
            {
                File.Delete(temp);
                if (policy == "copy" && EntryUtil.HashOf(local) != EntryUtil.BaseHashOf(local))
                {
                    index.AddConflict(JsonUtil.Obj("id", JsonUtil.RandomId(), "path", relPath, "conflictPath", null, "kind", "incoming-rejected", "peerId", JsonUtil.GetString(peer["id"], ""), "peerName", JsonUtil.GetString(peer["name"], ""), "localHash", EntryUtil.HashOf(local), "remoteHash", hash, "message", "双方同时修改，本机版本较新", "createdAt", JsonUtil.NowIso()));
                }
                index.Save();
                return JsonUtil.Obj("ok", true, "conflict", policy == "copy", "superseded", policy == "newest", "entry", local);
            }
            if (policy == "copy" && EntryUtil.HashOf(local) != EntryUtil.BaseHashOf(local)) PreserveConflict(folder, index, local, peer, "incoming");
            Versions.Backup(folder, relPath, "peer-overwrite");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            AtomicFile.Commit(temp, destination, hash, expected, EntryUtil.MtimeOf(incoming), delegate { check(); SafePath(folder["path"].ToString(), relPath); });
            incoming["baseHash"] = hash;
            incoming["updatedAt"] = JsonUtil.NowIso();
            index.Upsert(incoming);
            index.ObserveRev(EntryUtil.RevOf(incoming));
            index.Save();
            return JsonUtil.Obj("ok", true, "entry", incoming);
        }

        public JObject AcceptIncomingDelete(JObject peer, JObject folder, string relPath, JObject incoming)
        {
            lock (gate)
            {
                TransferGuard(peer, folder, true)();
                return AcceptIncomingDeleteCore(peer, folder, relPath, incoming);
            }
        }

        private JObject AcceptIncomingDeleteCore(JObject peer, JObject folder, string relPath, JObject incoming)
        {
            SafeFiles.Join(JsonUtil.GetString(folder["path"], ""), relPath);
            var index = IndexFor(folder);
            var local = index.Get(relPath);
            if (local == null)
            {
                incoming["path"] = relPath;
                incoming["deleted"] = true;
                incoming["size"] = 0;
                incoming["hash"] = null;
                incoming["updatedAt"] = JsonUtil.NowIso();
                index.Upsert(incoming);
                index.ObserveRev(EntryUtil.RevOf(incoming));
                index.Save();
                return JsonUtil.Obj("ok", true);
            }
            if (EntryUtil.Deleted(local)) return JsonUtil.Obj("ok", true);
            if (EntryUtil.Compare(incoming, local) <= 0)
            {
                index.AddConflict(JsonUtil.Obj("id", JsonUtil.RandomId(), "path", relPath, "conflictPath", null, "kind", "peer-delete", "peerId", JsonUtil.GetString(peer["id"], ""), "peerName", JsonUtil.GetString(peer["name"], ""), "localHash", EntryUtil.HashOf(local), "remoteHash", null, "message", "对方删除了文件，但本机有修改", "createdAt", JsonUtil.NowIso()));
                index.Save();
                return JsonUtil.Obj("ok", true, "conflict", true);
            }
            if (EntryUtil.HashOf(local) != EntryUtil.BaseHashOf(local))
            {
                index.AddConflict(JsonUtil.Obj("path", relPath, "conflictPath", null, "kind", "peer-delete", "peerId", JsonUtil.GetString(peer["id"], ""), "peerName", JsonUtil.GetString(peer["name"], ""), "localHash", EntryUtil.HashOf(local), "remoteHash", null, "message", "对方删除了文件，但本机有修改", "createdAt", JsonUtil.NowIso()));
                index.Save();
                return JsonUtil.Obj("ok", true, "conflict", true);
            }
            Versions.Backup(folder, relPath, "peer-delete");
            var target = SafePath(JsonUtil.GetString(folder["path"], ""), relPath);
            AtomicFile.DeleteUnchanged(target, EntryUtil.HashOf(local), TransferGuard(peer, folder, true));
            local["deleted"] = true;
            local["hash"] = null;
            local["size"] = 0;
            local["rev"] = EntryUtil.RevOf(incoming);
            local["origin"] = EntryUtil.OriginOf(incoming);
            local["baseHash"] = EntryUtil.BaseHashOf(local);
            local["updatedAt"] = JsonUtil.NowIso();
            index.Upsert(local);
            index.Save();
            return JsonUtil.Obj("ok", true);
        }

        public bool TestPeer(JObject peer)
        {
            try
            {
                new PeerClient(peer, Config.DeviceId).Ping();
                lock (gate) online[JsonUtil.GetString(peer["id"], "")] = true;
                return true;
            }
            catch
            {
                lock (gate) online[JsonUtil.GetString(peer["id"], "")] = false;
                return false;
            }
        }

        public JArray Manifest(JObject folder, JObject peer = null)
        {
            lock (gate) { if (peer != null) TransferGuard(peer, folder)(); return new JArray(IndexFor(folder).Entries.Values.Select(entry => entry.DeepClone())); }
        }

        public void ApplyAcks(JObject folder, JArray entries, JObject peer = null)
        {
            lock (gate)
            {
                if (peer != null) TransferGuard(peer, folder)();
                var index = IndexFor(folder);
                foreach (var incoming in entries.OfType<JObject>())
                {
                    var path = EntryUtil.PathOf(incoming);
                    var local = index.Get(path);
                    if (local == null || EntryUtil.Deleted(local) || EntryUtil.HashOf(local) != EntryUtil.HashOf(incoming)) continue;
                    local["baseHash"] = EntryUtil.HashOf(incoming);
                    local["rev"] = Math.Max(EntryUtil.RevOf(local), EntryUtil.RevOf(incoming));
                    index.Upsert(local);
                }
                index.Save();
            }
        }

        public JObject PairOutgoing(string host, int port, string code, string remoteFingerprint)
        {
            remoteFingerprint = DeviceSecurity.NormalizeFingerprint(remoteFingerprint);
            var device = JsonUtil.Obj("id", Config.DeviceId, "name", Config.DeviceName, "platform", "win32", "port", Config.Port, "fingerprint", fingerprint);
            var folders = new JArray();
            var token = JsonUtil.RandomToken(32);
            var response = PeerClient.Pair(host, port, code, token, device, folders, remoteFingerprint);
            var peer = response["device"] as JObject;
            if (peer == null) throw new Exception("对方返回的信息不完整");
            AddPeer(JsonUtil.GetString(peer["id"], ""), JsonUtil.GetString(peer["name"], ""), host, port, JsonUtil.GetString(response["token"], token), JsonUtil.GetString(peer["hostname"], ""));
            Config.GetPeer(JsonUtil.GetString(peer["id"], ""))["fingerprint"] = remoteFingerprint;
            Config.Save();
            MergeFolders(response["folders"] as JArray, JsonUtil.GetString(peer["id"], ""));
            AddActivity("device", "已连接设备", JsonUtil.GetString(peer["name"], ""), "success");
            SyncAll(true);
            return peer;
        }

        public JObject PairIncoming(JObject body, string ip)
        {
            var code = JsonUtil.GetString(body["code"], "");
            var device = body["device"] as JObject;
            if (device == null) throw new Exception("配对信息不完整");
            var peerId = JsonUtil.GetString(device["id"], "");
            var token = JsonUtil.GetString(body["token"], "");
            Guid parsedId;
            if (!Guid.TryParse(peerId, out parsedId) || peerId == Config.DeviceId || token.Length < 40 || token.Length > 128) throw new Exception("配对设备身份无效");
            var remoteFingerprint = DeviceSecurity.NormalizeFingerprint(JsonUtil.GetString(device["fingerprint"], ""));
            if (!validatePairCode(code)) throw new Exception("配对码无效、已过期或尝试过于频繁，请在对方设备刷新");
            AddPeer(peerId, JsonUtil.GetString(device["name"], "设备"), ip, JsonUtil.GetInt(device["port"], 55220), token, JsonUtil.GetString(device["hostname"], ""));
            Config.GetPeer(peerId)["fingerprint"] = remoteFingerprint;
            MergeFolders(body["folders"] as JArray, peerId);
            AddActivity("device", "设备已连接", ip, "success");
            var response = JsonUtil.Obj("ok", true, "token", token, "device", JsonUtil.Obj("id", Config.DeviceId, "name", Config.DeviceName, "platform", "win32", "port", Config.Port, "fingerprint", fingerprint), "folders", new JArray());
            Config.Save();
            return response;
        }

        public void AddPeer(string id, string name, string host, int port, string token, string hostname)
        {
            var peer = Config.GetPeer(id);
            if (peer == null)
            {
                peer = JsonUtil.Obj("id", id, "name", name, "hostname", hostname, "host", host, "port", port, "token", token, "pairedAt", JsonUtil.NowIso(), "lastSeenAt", null, "paused", false, "autoSync", true, "rttMs", null);
                Config.Peers.Add(peer);
            }
            else
            {
                peer["name"] = name;
                peer["host"] = host;
                peer["port"] = port;
                peer["token"] = token;
                peer["hostname"] = hostname;
            }
            Config.Save();
        }

        public void MergeFolders(JArray offers, string peerId)
        {
            if (offers == null) return;
            var peer = Config.GetPeer(peerId);
            if (peer == null) throw new Exception("设备未配对");
            var pending = peer["offeredFolders"] as JArray ?? new JArray();
            foreach (var offer in offers.OfType<JObject>())
            {
                var id = JsonUtil.GetString(offer["id"], "");
                Guid parsed;
                if (!Guid.TryParse(id, out parsed)) continue;
                foreach (var old in pending.OfType<JObject>().Where(item => JsonUtil.GetString(item["id"], "") == id).ToList()) pending.Remove(old);
                if (pending.Count >= 100) throw new Exception("待确认的文件夹过多");
                pending.Add(JsonUtil.Obj("id", id, "name", JsonUtil.GetString(offer["name"], "同步文件夹"), "peerId", peerId));
            }
            peer["offeredFolders"] = pending;
            Config.Save();
        }

        public JArray PendingFolders()
        {
            return new JArray(Config.Peers.OfType<JObject>().SelectMany(peer => ((peer["offeredFolders"] as JArray) ?? new JArray()).OfType<JObject>()).Where(offer =>
            {
                var folder = Config.GetFolder(JsonUtil.GetString(offer["id"], ""));
                return folder == null || !((folder["peerIds"] as JArray) ?? new JArray()).Any(id => id.ToString() == JsonUtil.GetString(offer["peerId"], ""));
            }).Select(offer => offer.DeepClone()));
        }

        public JObject FolderShare(JObject peer, JObject body)
        {
            lock (gate) return FolderShareCore(peer, body);
        }

        private JObject FolderShareCore(JObject peer, JObject body)
        {
            if (!object.ReferenceEquals(Config.GetPeer(peer["id"].ToString()), peer) || JsonUtil.GetBool(peer["paused"], false)) throw new OperationCanceledException("设备授权已变更。");
            var folder = body["folder"] as JObject;
            if (folder == null) throw new Exception("共享信息不完整");
            var id = JsonUtil.GetString(folder["id"], "");
            var shared = body["shared"] == null || body["shared"].Value<bool>();
            var local = Config.GetFolder(id);
            if (!shared)
            {
                var offers = peer["offeredFolders"] as JArray;
                if (offers != null) foreach (var offer in offers.OfType<JObject>().Where(item => JsonUtil.GetString(item["id"], "") == id).ToList()) offers.Remove(offer);
                if (local != null)
                {
                    var peerIds = local["peerIds"] as JArray;
                    if (peerIds != null)
                    {
                        var kept = peerIds.Where(value => value.ToString() != JsonUtil.GetString(peer["id"], "")).ToArray();
                        local["peerIds"] = new JArray(kept);
                        InvalidateFolder(local);
                        Config.Save();
                    }
                }
                return JsonUtil.Obj("ok", true);
            }
            MergeFolders(new JArray(folder), JsonUtil.GetString(peer["id"], ""));
            return JsonUtil.Obj("ok", true);
        }

        public void SetStatus(string state, string message, string file)
        {
            lock (gate)
            {
                Status["state"] = state;
                Status["message"] = message;
                Status["currentFile"] = file;
            }
        }

        public void AddActivity(string type, string title, string detail, string level)
        {
            lock (gate)
            {
                Activity.Insert(0, JsonUtil.Obj("id", JsonUtil.RandomId(), "type", type, "title", title, "detail", detail, "level", level, "at", JsonUtil.NowIso()));
                while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
            }
        }

        private static string SafePath(string root, string relative)
        {
            return SafeFiles.Join(root, relative);
        }
    }
}
