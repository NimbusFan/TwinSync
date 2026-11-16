using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed partial class SyncEngine
    {
        private sealed class PendingActivity
        {
            internal JObject Data;
            internal DateTime Updated = DateTime.UtcNow;
            internal readonly Dictionary<string, long> Files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
        private readonly Dictionary<string, PendingActivity> pendingActivity = new Dictionary<string, PendingActivity>();
        private JObject runMetrics;
        private sealed class DirectoryNames { internal JArray Source; internal HashSet<string> Paths; }
        private readonly Dictionary<string, DirectoryNames> knownActivityDirectories = new Dictionary<string, DirectoryNames>();
        private static string DisplayName(JObject peer, ConfigStore config) { var name = peer == null ? config.DeviceName : (string)peer["name"]; return string.IsNullOrWhiteSpace(name) ? (peer == null ? "本机" : "未命名设备") : name; }

        internal void QueueFileChanges(JObject folder, JObject peer, JArray changes)
        {
            lock (gate)
            {
                var actor = DisplayName(peer, Config);
                foreach (var change in changes.OfType<JObject>())
                {
                    var relative = JsonUtil.GetString(change["path"], "");
                    if (TransferCache.Reserved(relative)) continue;
                    var action = JsonUtil.GetString(change["action"], "modified");
                    var isDirectory = (string)change["kind"] == "directory" || change["group"] != null;
                    var path = change["group"] == null ? relative : (string)change["group"];
                    if (!isDirectory && action == "added")
                    {
                        var active = pendingActivity.Where(pair => pair.Key.StartsWith(folder["id"] + "|" + (peer == null ? Config.DeviceId : (string)peer["id"]) + "|", StringComparison.Ordinal)
                            && (string)pair.Value.Data["kind"] == "directory" && (string)pair.Value.Data["action"] == "added"
                            && (string)pair.Value.Data["path"] != "" && relative.StartsWith((string)pair.Value.Data["path"] + "/", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => ((string)pair.Value.Data["path"]).Length).FirstOrDefault();
                        if (active.Value != null) { isDirectory = true; path = (string)active.Value.Data["path"]; }
                    }
                    var key = folder["id"] + "|" + (peer == null ? Config.DeviceId : (string)peer["id"]) + "|" + (isDirectory ? "directory" : "file") + "|" + path;
                    PendingActivity item;
                    if (!pendingActivity.TryGetValue(key, out item))
                    {
                        if (pendingActivity.Count >= 256) FlushActivity(true);
                        pendingActivity[key] = item = new PendingActivity { Data = JsonUtil.Obj("actor", actor, "folder", folder["name"], "path", path, "action", action, "kind", isDirectory ? "directory" : "file", "direction", peer == null ? "local" : "incoming") };
                    }
                    else if ((string)item.Data["action"] != "added" || action == "deleted") item.Data["action"] = action;
                    if ((string)change["kind"] != "directory") item.Files[relative] = Math.Max(0, JsonUtil.GetLong(change["size"], 0));
                    item.Updated = DateTime.UtcNow;
                }
            }
        }
        internal void FlushActivity(bool force = false)
        {
            lock (gate)
            {
                foreach (var pair in pendingActivity.ToArray())
                {
                    if (!force && (syncing || DateTime.UtcNow - pair.Value.Updated < TimeSpan.FromMilliseconds(1800))) continue;
                    var data = pair.Value.Data; var action = (string)data["action"];
                    data["files"] = pair.Value.Files.Count; data["bytes"] = pair.Value.Files.Values.Sum();
                    var path = (string)data["path"]; var kind = (string)data["kind"];
                    var verb = action == "added" ? "添加了" : action == "deleted" ? "删除了" : "修改了";
                    var name = string.IsNullOrEmpty(path) ? (string)data["folder"] : path;
                    AddActivity("file", data["actor"] + " " + verb + (kind == "directory" ? "文件夹「" : "文件「") + name + "」",
                        (string)data["folder"], "info", data);
                    pendingActivity.Remove(pair.Key);
                }
            }
        }
        private void RecordIncoming(JObject peer, JObject folder, JObject incoming, JObject before, bool deleted)
        {
            var path = EntryUtil.PathOf(incoming);
            if (!deleted)
            {
                var index = IndexFor(folder); var known = index.Root["knownDirectories"] as JArray;
                if (known == null) index.Root["knownDirectories"] = known = new JArray();
                DirectoryNames cache; var id = (string)folder["id"];
                if (!knownActivityDirectories.TryGetValue(id, out cache) || !object.ReferenceEquals(cache.Source, known))
                    knownActivityDirectories[id] = cache = new DirectoryNames { Source = known, Paths = new HashSet<string>(known.Values<string>(), StringComparer.OrdinalIgnoreCase) };
                var parents = path; var changed = false;
                for (var slash = parents.LastIndexOf('/'); slash > 0; slash = parents.LastIndexOf('/'))
                {
                    parents = parents.Substring(0, slash);
                    if (cache.Paths.Add(parents)) { known.Add(parents); changed = true; }
                }
                if (changed) index.Save();
            }
            var change = JsonUtil.Obj("path", path, "action", deleted ? "deleted" : before == null || EntryUtil.Deleted(before) ? "added" : "modified", "size", deleted ? EntryUtil.SizeOf(before) : EntryUtil.SizeOf(incoming));
            if (incoming["changeRoot"] != null && incoming["changeRoot"].Type == JTokenType.String) change["group"] = incoming["changeRoot"].DeepClone();
            QueueFileChanges(folder, peer, new JArray(change));
        }
        private void CountOperation(string action, JObject local, JObject remote)
        {
            if (runMetrics == null) return;
            runMetrics["checked"] = JsonUtil.GetInt(runMetrics["checked"], 0) + 1;
            var entry = action == "pull" ? remote : local;
            if (action != "pull" && action != "push" || entry == null) return;
            if (EntryUtil.Deleted(entry))
            {
                var other = action == "pull" ? local : remote;
                if (other != null && !EntryUtil.Deleted(other)) runMetrics["deleted"] = JsonUtil.GetInt(runMetrics["deleted"], 0) + 1;
                return;
            }
            runMetrics["files"] = JsonUtil.GetInt(runMetrics["files"], 0) + 1;
            runMetrics["bytes"] = JsonUtil.GetLong(runMetrics["bytes"], 0) + EntryUtil.SizeOf(entry);
            var key = action == "pull" ? "downloaded" : "uploaded";
            runMetrics[key] = JsonUtil.GetInt(runMetrics[key], 0) + 1;
        }
        private void ReportPeerActivity(JObject peer, JObject folder, JObject before, long duration, bool succeeded)
        {
            if (requestedPause || !IsOnline((string)peer["id"]) || runMetrics == null) return;
            var metrics = new JObject();
            foreach (var key in new[] { "files", "bytes", "deleted", "checked", "uploaded", "downloaded" }) metrics[key] = Math.Max(0, JsonUtil.GetLong(runMetrics[key], 0) - JsonUtil.GetLong(before[key], 0));
            metrics["durationMs"] = duration;
            try { new PeerClient(peer, Config.DeviceId, TransferGuard(peer, folder)).RequestJson("/peer/activity-finish", System.Net.Http.HttpMethod.Post, JsonUtil.Obj("folder", folder["id"], "metrics", metrics, "succeeded", succeeded), 3000); }
            catch { /* Optional reporting must not change synchronization results on older peers. */ }
        }
        internal void ReceiveSyncActivity(JObject peer, JObject folder, JObject supplied, bool succeeded)
        {
            lock (gate)
            {
                FlushActivity(true);
                var data = new JObject();
                foreach (var key in new[] { "files", "bytes", "deleted", "checked", "durationMs" }) data[key] = Math.Max(0, JsonUtil.GetLong(supplied[key], 0));
                data["uploaded"] = Math.Max(0, JsonUtil.GetLong(supplied["downloaded"], 0));
                data["downloaded"] = Math.Max(0, JsonUtil.GetLong(supplied["uploaded"], 0));
                data["scope"] = folder["name"] + " ↔ " + DisplayName(peer, Config);
                data["direction"] = "incoming";
                AddActivity("sync", succeeded ? "与 " + DisplayName(peer, Config) + " 同步完成" : "与 " + DisplayName(peer, Config) + " 同步结束，存在未完成项目", (string)data["scope"], succeeded ? "success" : "warn", data);
            }
        }
        private void PeerPresence(JObject peer, bool connected)
        {
            lock (gate)
            {
                var id = (string)peer["id"]; bool previous; var known = online.TryGetValue(id, out previous); online[id] = connected;
                if (known && previous == connected || !known && !connected) return;
                var names = Config.Folders.OfType<JObject>().Where(folder => ((folder["peerIds"] as JArray) ?? new JArray()).Any(value => (string)value == id)).Select(folder => (string)folder["name"]).ToArray();
                AddActivity("device", DisplayName(peer, Config) + (connected ? " 已上线" : " 已离线"),
                    "共享文件夹：" + (names.Length == 0 ? "尚未共享" : string.Join("、", names.Take(20))), connected ? "success" : "warn",
                    JsonUtil.Obj("actor", DisplayName(peer, Config), "action", connected ? "online" : "offline", "scope", string.Join("、", names.Take(20))));
            }
        }
    }
}
