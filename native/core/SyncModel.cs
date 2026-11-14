using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class EntryUtil
    {
        public static string PathOf(JObject entry) { return JsonUtil.GetString(entry == null ? null : entry["path"], ""); }
        public static string HashOf(JObject entry) { return JsonUtil.GetString(entry == null ? null : entry["hash"], ""); }
        public static string OriginOf(JObject entry) { return JsonUtil.GetString(entry == null ? null : entry["origin"], ""); }
        public static string BaseHashOf(JObject entry) { return JsonUtil.GetString(entry == null ? null : entry["baseHash"], ""); }
        public static int RevOf(JObject entry) { return entry == null ? 0 : JsonUtil.GetInt(entry["rev"], 0); }
        public static long SizeOf(JObject entry) { return entry == null ? 0 : JsonUtil.GetLong(entry["size"], 0); }
        public static double MtimeOf(JObject entry) { return entry == null ? 0 : (entry["mtimeMs"] == null ? 0 : entry["mtimeMs"].Value<double>()); }
        public static bool Deleted(JObject entry) { return entry != null && JsonUtil.GetBool(entry["deleted"], false); }

        public static bool SameContent(JObject local, JObject remote)
        {
            if (local == null || remote == null) return false;
            if (Deleted(local) != Deleted(remote)) return false;
            if (Deleted(local)) return true;
            return string.Equals(HashOf(local), HashOf(remote), StringComparison.Ordinal);
        }

        public static int Compare(JObject local, JObject remote)
        {
            var rev = RevOf(local).CompareTo(RevOf(remote));
            if (rev != 0) return rev > 0 ? 1 : -1;
            var origin = string.CompareOrdinal(OriginOf(local), OriginOf(remote));
            if (origin != 0) return origin > 0 ? 1 : -1;
            var hash = string.CompareOrdinal(HashOf(local), HashOf(remote));
            if (hash != 0) return hash > 0 ? 1 : -1;
            return 0;
        }

        public static bool BothChanged(JObject local, JObject remote)
        {
            return local != null && remote != null && !Deleted(local) && !Deleted(remote) && HashOf(local) != HashOf(remote)
                && HashOf(local) != BaseHashOf(local) && HashOf(remote) != BaseHashOf(remote);
        }

        public static int CompareForSync(JObject local, JObject remote, string policy)
        {
            if (policy == "newest" && BothChanged(local, remote))
            {
                var time = MtimeOf(local).CompareTo(MtimeOf(remote));
                if (time != 0) return time;
            }
            return Compare(local, remote);
        }

        public static string Decide(JObject local, JObject remote, string mode, string policy = "copy")
        {
            if (local == null && remote == null) return "same";
            if (local == null) return mode == "send-only" ? "none" : "pull";
            if (remote == null) return mode == "receive-only" ? "none" : "push";
            if (SameContent(local, remote)) return "same";
            var comparison = CompareForSync(local, remote, policy);
            if (comparison == 0) return "same";
            if (comparison > 0) return mode == "receive-only" ? "none" : "push";
            return mode == "send-only" ? "none" : "pull";
        }

        public static string ConflictPath(string relPath, string deviceName, System.Collections.Generic.HashSet<string> existing)
        {
            var normalized = relPath.Replace('\\', '/');
            var directory = Path.GetDirectoryName(normalized);
            var name = Path.GetFileNameWithoutExtension(normalized);
            var extension = Path.GetExtension(normalized);
            var safeName = string.IsNullOrEmpty(deviceName) ? "device" : deviceName;
            foreach (var invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid.ToString(), "");
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");
            var candidate = string.Format("{0} (冲突 来自 {1} {2}){3}", name, safeName, stamp, extension);
            if (!string.IsNullOrEmpty(directory)) candidate = directory.Replace('\\', '/') + "/" + candidate;
            var counter = 2;
            while (existing.Contains(candidate.ToLowerInvariant()))
            {
                candidate = string.Format("{0} (冲突 来自 {1} {2} {3}){4}", name, safeName, stamp, counter, extension);
                if (!string.IsNullOrEmpty(directory)) candidate = directory.Replace('\\', '/') + "/" + candidate;
                counter += 1;
            }
            return candidate;
        }
    }
}
