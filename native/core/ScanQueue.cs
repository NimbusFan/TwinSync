using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class ScanQueue
    {
        private sealed class Pending
        {
            internal HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool Full = true, Watching;
            internal string Signature;
            internal DateTime LastFull;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        internal long FullScans, PathScans;
        private Pending Get(string id) { Pending item; if (!pending.TryGetValue(id, out item)) pending[id] = item = new Pending(); return item; }
        internal void Watching(string id, bool value) { lock (gate) Get(id).Watching = value; }
        internal void Enqueue(string id, string root, string path)
        {
            lock (gate)
            {
                var item = Get(id);
                if (path == null) { item.Full = true; return; }
                var boundary = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) { item.Full = true; return; }
                var relative = path.Substring(boundary.Length).Replace('\\', '/');
                if (TransferCache.Reserved(relative)) return;
                if (relative.Split('/').Any(p => p.IndexOf(".twinsync-part-", StringComparison.OrdinalIgnoreCase) >= 0)) return;
                if (item.Paths.Count >= 4096) { item.Full = true; item.Paths.Clear(); } else item.Paths.Add(relative);
            }
        }
        internal bool Scan(JObject folder, IndexStore index, ConfigStore config, bool manual, bool forceHash = false)
        {
            var id = (string)folder["id"]; Pending item; bool full; string[] paths;
            var signature = (string)folder["path"] + "\n" + folder["ignore"];
            lock (gate)
            {
                item = Get(id);
                full = manual || item.Full || item.Signature != signature || DateTime.UtcNow - item.LastFull > TimeSpan.FromSeconds(item.Watching ? 600 : 30);
                paths = item.Paths.ToArray(); item.Paths.Clear(); item.Full = false;
            }
            if (!full && paths.Length == 0) return false;
            try
            {
                if (full) { Scanner.Scan(folder, index, config, forceHash); FullScans++; }
                else { Scanner.ScanPaths(folder, index, config, paths); PathScans++; }
                lock (gate) { item.Signature = signature; if (full) item.LastFull = DateTime.UtcNow; }
                return true;
            }
            catch { lock (gate) { item.Full = true; foreach (var path in paths) item.Paths.Add(path); } throw; }
        }
        internal void Forget(string id) { lock (gate) pending.Remove(id); }
    }
}
