using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class VersionStore
    {
        private readonly string root, metaFile;
        private readonly ConfigStore config;
        private JArray items;
        private readonly object gate = new object();
        private readonly Dictionary<string, int> pinned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, JObject> reserved = new Dictionary<string, JObject>();
        private readonly Dictionary<string, int> reservations = new Dictionary<string, int>();
        private DateTime lastMaintenance = DateTime.MinValue;

        public VersionStore(ConfigStore config)
        {
            this.config = config; root = config.VersionsDir; metaFile = Path.Combine(root, "index.json");
            Directory.CreateDirectory(root); items = JsonUtil.LoadArray(metaFile);
        }

        public int Count { get { lock (gate) return items.Count; } }
        public JArray Items { get { lock (gate) return (JArray)items.DeepClone(); } }
        private string BlobPath(string hash)
        {
            if (!Regex.IsMatch(hash ?? "", "^[a-fA-F0-9]{64}$")) throw new IOException("历史版本标识无效。");
            hash = hash.ToLowerInvariant();
            return SafeFiles.Join(root, "blobs/" + hash.Substring(0, 2) + "/" + hash);
        }

        public JObject Backup(JObject folder, string relPath, string reason)
        {
            lock (gate)
            {
                if (!JsonUtil.GetBool(config.Settings["versioningEnabled"], true)) return null;
                var source = SafeFiles.UserPath(folder["path"].ToString(), relPath);
                if (!File.Exists(source)) return null;
                var stat = new FileInfo(source); var hash = JsonUtil.Sha256File(source); var blob = BlobPath(hash);
                if (!File.Exists(blob)) AtomicFile.CopyVerified(source, blob, hash, null, 0, null);
                else if (!string.Equals(JsonUtil.Sha256File(blob), hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("已有历史版本校验失败，未覆盖当前文件。");
                var item = JsonUtil.Obj("id", JsonUtil.RandomId(), "folderId", folder["id"], "folderName", folder["name"],
                    "path", relPath.Replace('\\', '/'), "hash", hash, "size", stat.Length, "reason", reason,
                    "savedAt", JsonUtil.NowIso(), "originalMtimeMs", new DateTimeOffset(stat.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                var candidate = (JArray)items.DeepClone(); candidate.Insert(0, item);
                SaveTrimmed(candidate);
                return (JObject)item.DeepClone();
            }
        }

        private JArray Trim(JArray source)
        {
            var max = Math.Max(1, Math.Min(200, JsonUtil.GetInt(config.Settings["maxVersionsPerFile"], 10)));
            var retention = Math.Max(1, Math.Min(3650, JsonUtil.GetInt(config.Settings["versionRetentionDays"], 30)));
            var cutoff = DateTime.UtcNow.AddDays(-retention); var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var kept = new JArray();
            foreach (var item in source.OfType<JObject>())
            {
                var key = item["folderId"] + "\n" + item["path"]; int count; counts.TryGetValue(key, out count);
                DateTime saved;
                if (DateTime.TryParse(JsonUtil.GetString(item["savedAt"], ""), out saved) && saved.ToUniversalTime() < cutoff) continue;
                if (count >= max) continue;
                counts[key] = count + 1; kept.Add(item.DeepClone());
            }
            return kept;
        }

        private HashSet<string> References()
        {
            var references = new HashSet<string>(items.OfType<JObject>().Select(item => JsonUtil.GetString(item["hash"], "")), StringComparer.OrdinalIgnoreCase);
            references.UnionWith(pinned.Keys); return references;
        }

        private void DeleteUnreferenced(IEnumerable<string> hashes)
        {
            var references = References();
            foreach (var hash in hashes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (references.Contains(hash) || !Regex.IsMatch(hash ?? "", "^[a-fA-F0-9]{64}$")) continue;
                try { var path = BlobPath(hash); if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { } // An in-use blob is retried by scheduled maintenance.
                catch (UnauthorizedAccessException) { }
            }
        }

        private void SaveTrimmed(JArray candidate)
        {
            var kept = Trim(candidate);
            // Persist references before reclaiming any content; a failed save retains every old blob.
            JsonUtil.SaveAtomic(metaFile, kept); items = kept;
            DeleteUnreferenced(candidate.OfType<JObject>().Select(item => JsonUtil.GetString(item["hash"], "")));
        }

        internal void Maintain(bool force = false)
        {
            lock (gate)
            {
                if (!force && (DateTime.UtcNow - lastMaintenance).TotalHours < 1) return;
                lastMaintenance = DateTime.UtcNow;
                SaveTrimmed(items);
                var blobs = SafeFiles.Join(root, "blobs");
                if (Directory.Exists(blobs))
                {
                    var hashes = new List<string>();
                    foreach (var file in SafeFiles.Enumerate(blobs))
                    {
                        var hash = Path.GetFileName(file);
                        if (Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") && string.Equals(file, BlobPath(hash), StringComparison.OrdinalIgnoreCase)) hashes.Add(hash);
                    }
                    DeleteUnreferenced(hashes);
                }
                lastMaintenance = DateTime.UtcNow;
            }
        }

        private JObject FindCore(string id) { JObject held; return items.OfType<JObject>().FirstOrDefault(item => item["id"].ToString() == id) ?? (reserved.TryGetValue(id, out held) ? held : null); }
        private void Pin(string hash) { int n; pinned.TryGetValue(hash, out n); pinned[hash] = n + 1; }
        private void Unpin(string hash) { if (pinned[hash] == 1) pinned.Remove(hash); else pinned[hash]--; }
        private sealed class Reservation : IDisposable
        {
            internal VersionStore Owner; internal string[] Ids;
            public void Dispose()
            {
                var owner = System.Threading.Interlocked.Exchange(ref Owner, null); if (owner == null) return;
                lock (owner.gate)
                {
                    var hashes = Ids.Select(id => (string)owner.reserved[id]["hash"]).ToArray();
                    foreach (var id in Ids) { owner.Unpin((string)owner.reserved[id]["hash"]); if (--owner.reservations[id] == 0) { owner.reserved.Remove(id); owner.reservations.Remove(id); } }
                    owner.DeleteUnreferenced(hashes);
                }
            }
        }
        internal IDisposable Retain(IEnumerable<string> ids)
        {
            lock (gate)
            {
                var found = ids.Distinct().Select(FindCore).Where(v => v != null).ToArray();
                foreach (var item in found) { var id = (string)item["id"]; int n; reservations.TryGetValue(id, out n); reservations[id] = n + 1; reserved[id] = (JObject)item.DeepClone(); Pin((string)item["hash"]); }
                return new Reservation { Owner = this, Ids = found.Select(v => (string)v["id"]).ToArray() };
            }
        }
        public JObject Find(string id) { lock (gate) { var item = FindCore(id); return item == null ? null : (JObject)item.DeepClone(); } }

        public JObject Restore(string id, JObject folder)
        {
            lock (gate)
            {
                var found = FindCore(id); if (found == null) throw new Exception("历史版本不存在");
                var item = (JObject)found.DeepClone();
                if (item["folderId"].ToString() != folder["id"].ToString()) throw new Exception("历史版本与文件夹不匹配");
                var hash = item["hash"].ToString(); var blob = BlobPath(hash);
                if (!File.Exists(blob)) throw new Exception("历史版本文件缺失");
                var target = SafeFiles.UserPath(folder["path"].ToString(), item["path"].ToString());
                var expected = AtomicFile.HashOrNull(target); Pin(hash);
                try
                {
                    Backup(folder, item["path"].ToString(), "before-restore");
                    AtomicFile.CopyVerified(blob, target, hash, expected, JsonUtil.GetLong(item["originalMtimeMs"], 0), delegate { SafeFiles.UserPath(folder["path"].ToString(), item["path"].ToString()); });
                    var retained = FindCore(id);
                    if (retained != null) { retained["restoredAt"] = JsonUtil.NowIso(); retained["restoreCount"] = JsonUtil.GetInt(retained["restoreCount"], 0) + 1; JsonUtil.SaveAtomic(metaFile, items); }
                    return item;
                }
                finally { Unpin(hash); DeleteUnreferenced(new[] { hash }); }
            }
        }

        public byte[] Content(string id)
        {
            using (var input = OpenContent(id))
            {
                if (input.Length > 65536) throw new IOException("请使用分段预览或流式下载读取此版本。");
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }

        internal FileStream OpenContent(string id)
        {
            lock (gate) { var item = FindCore(id); if (item == null) throw new IOException("历史版本不存在"); return File.OpenRead(BlobPath((string)item["hash"])); }
        }
        public List<JObject> List(string folderId, string relPath)
        {
            lock (gate) return items.OfType<JObject>().Where(item => (string.IsNullOrEmpty(folderId) || item["folderId"].ToString() == folderId) && (string.IsNullOrEmpty(relPath) || item["path"].ToString() == relPath)).Select(item => (JObject)item.DeepClone()).ToList();
        }
    }
}
