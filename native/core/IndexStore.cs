using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class IndexStore
    {
        public string FolderId { get; private set; }
        public string FilePath { get; private set; }
        public JObject Root { get; private set; }
        public Dictionary<string, JObject> Entries { get; private set; }
        public JArray Conflicts { get; private set; }

        public IndexStore(string stateDir, string folderId)
        {
            FolderId = folderId;
            FilePath = Path.Combine(stateDir, "index-" + folderId + ".json");
            Root = JsonUtil.LoadObject(FilePath);
            if (Root == null)
            {
                Root = JsonUtil.Obj(
                    "version", 1,
                    "folderId", folderId,
                    "lamport", 0,
                    "lastScanAt", null,
                    "lastSyncAt", null,
                    "savedAt", JsonUtil.NowIso(),
                    "entries", new JArray(),
                    "conflicts", new JArray());
            }
            Entries = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var entries = Root["entries"] as JArray;
            if (entries != null)
            {
                foreach (var token in entries.OfType<JObject>())
                {
                    var path = JsonUtil.GetString(token["path"], "");
                    if (!string.IsNullOrEmpty(path)) Entries[path] = token;
                }
                entries.RemoveAll();
            }
            Root.Remove("entries");
            Conflicts = Root["conflicts"] as JArray ?? new JArray();
        }

        public int FileCount
        {
            get { return Entries.Values.Count(entry => !JsonUtil.GetBool(entry["deleted"], false)); }
        }

        public long TotalBytes
        {
            get { return Entries.Values.Where(entry => !JsonUtil.GetBool(entry["deleted"], false)).Sum(entry => JsonUtil.GetLong(entry["size"], 0)); }
        }

        public int ConflictCount { get { return Conflicts.Count; } }
        public string LastScanAt
        {
            get { return JsonUtil.GetString(Root["lastScanAt"], null); }
            set { Root["lastScanAt"] = value; }
        }
        public string LastSyncAt
        {
            get { return JsonUtil.GetString(Root["lastSyncAt"], null); }
            set { Root["lastSyncAt"] = value; }
        }

        public JObject Get(string path)
        {
            JObject value;
            return Entries.TryGetValue(path, out value) ? value : null;
        }

        public void Upsert(JObject entry)
        {
            var path = JsonUtil.GetString(entry["path"], "");
            if (string.IsNullOrEmpty(path)) return;
            Entries[path] = entry;
        }

        public int NextRev()
        {
            var lamport = JsonUtil.GetInt(Root["lamport"], 0) + 1;
            Root["lamport"] = lamport;
            return lamport;
        }

        public void ObserveRev(int rev)
        {
            if (rev > JsonUtil.GetInt(Root["lamport"], 0)) Root["lamport"] = rev;
        }

        public void AddConflict(JObject conflict)
        {
            Conflicts.Add(conflict);
        }

        public void Save()
        {
            Root["savedAt"] = JsonUtil.NowIso();
            Root["conflicts"] = Conflicts;
            JsonUtil.SaveAtomic(FilePath, delegate(JsonTextWriter writer)
            {
                writer.WriteStartObject();
                foreach (var property in Root.Properties()) property.WriteTo(writer);
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                foreach (var entry in Entries.Values) entry.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
    }
}
