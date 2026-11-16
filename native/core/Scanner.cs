using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class Scanner
    {
        private static readonly string[] DefaultPatterns = new[]
        {
            "desktop.ini", "thumbs.db", ".ds_store", "~$*", "*.tmp", "*.twinsync-part-*"
        };

        public static int Scan(JObject folder, IndexStore index, ConfigStore config, bool force, Action check = null, Action<string> retry = null)
        { return ScanCore(folder, index, config, force, null, check, retry); }

        public static int ScanPaths(JObject folder, IndexStore index, ConfigStore config, IEnumerable<string> paths, Action check = null, Action<string> retry = null)
        {
            var scopes = paths.Select(p => p.Replace('\\', '/').TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            scopes = scopes.Where(p => !scopes.Any(parent => parent != p && p.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            return ScanCore(folder, index, config, true, scopes, check, retry);
        }

        private static int ScanCore(JObject folder, IndexStore index, ConfigStore config, bool force, string[] scopes, Action check, Action<string> retry)
        {
            var root = JsonUtil.GetString(folder["path"], "");
            if (!Directory.Exists(root)) throw new Exception("同步目录不存在");
            var matcher = new IgnoreMatcher(folder["ignore"] as JArray);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var metadataChanged = false;
            SafeFiles.Join(root, "");
            var files = scopes == null ? SafeFiles.Enumerate(root) : scopes.SelectMany(relative => ChangedFiles(root, relative));
            foreach (var file in files)
            {
                var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                if (TransferCache.Reserved(relative)) continue;
                if (matcher.IsIgnored(relative, false)) continue;
                if (check != null) check();
                FileInfo stat; JObject local; long mtime; string hash;
                try
                {
                    stat = new FileInfo(file); if (!stat.Exists) continue;
                    local = index.Get(relative);
                    mtime = new DateTimeOffset(stat.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                    hash = local == null || force || EntryUtil.SizeOf(local) != stat.Length || Math.Abs(EntryUtil.MtimeOf(local) - mtime) > 1
                        ? Hash(file, check) : EntryUtil.HashOf(local);
                    if (SyncEngine.Missing(file)) continue;
                    var after = new FileInfo(file);
                    if (after.Length != stat.Length || after.LastWriteTimeUtc != stat.LastWriteTimeUtc)
                    { seen.Add(relative); if (retry != null) retry(relative); continue; }
                    seen.Add(relative);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                catch (IOException error)
                {
                    var code = error.HResult & 65535;
                    if (code != 32 && code != 33) throw;
                    seen.Add(relative); if (retry != null) retry(relative); continue;
                }
                if (local != null && !EntryUtil.Deleted(local) && EntryUtil.HashOf(local) == hash && EntryUtil.SizeOf(local) == stat.Length)
                {
                    if (EntryUtil.MtimeOf(local) != mtime) metadataChanged = true;
                    local["mtimeMs"] = mtime;
                    continue;
                }
                var rev = index.NextRev();
                var entry = JsonUtil.Obj(
                    "path", relative,
                    "type", "file",
                    "size", stat.Length,
                    "mtimeMs", mtime,
                    "hash", hash,
                    "rev", rev,
                    "origin", config.DeviceId,
                    "deleted", false,
                    "baseHash", local == null ? "" : EntryUtil.HashOf(local),
                    "updatedAt", JsonUtil.NowIso());
                index.Upsert(entry);
                count += 1;
            }
            if (check != null) check();
            if (!Directory.Exists(root)) throw new IOException("同步根目录已不可用，未生成删除记录。");
            foreach (var entry in index.Entries.Values.ToList())
            {
                if (TransferCache.Reserved(EntryUtil.PathOf(entry))) continue;
                if (scopes != null && !scopes.Any(p => string.Equals(p, EntryUtil.PathOf(entry), StringComparison.OrdinalIgnoreCase) || EntryUtil.PathOf(entry).StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))) continue;
                if (EntryUtil.Deleted(entry) || seen.Contains(EntryUtil.PathOf(entry))) continue;
                if (matcher.IsIgnored(EntryUtil.PathOf(entry), false)) continue;
                var rev = index.NextRev();
                var tombstone = (JObject)entry.DeepClone();
                tombstone["deleted"] = true;
                tombstone["size"] = 0;
                tombstone["hash"] = null;
                tombstone["baseHash"] = EntryUtil.HashOf(entry);
                tombstone["rev"] = rev;
                tombstone["origin"] = config.DeviceId;
                tombstone["updatedAt"] = JsonUtil.NowIso();
                index.Upsert(tombstone);
                count += 1;
            }
            index.LastScanAt = JsonUtil.NowIso();
            if (scopes == null || count > 0 || metadataChanged) index.Save();
            return count;
        }

        private static string Hash(string file, Action check)
        {
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var buffer = new byte[65536]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { if (check != null) check(); sha.TransformBlock(buffer, 0, read, null, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0); return ResumableTransfers.Hex(sha.Hash);
            }
        }

        internal static IEnumerable<string> VisibleDirectories(JObject folder, string directory)
        {
            var root = (string)folder["path"]; var location = SafeFiles.UserPath(root, directory);
            var matcher = new IgnoreMatcher(folder["ignore"] as JArray);
            string[] children;
            try { children = Directory.GetDirectories(location); } catch (DirectoryNotFoundException) { yield break; }
            foreach (var child in children)
            {
                var relative = child.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
                if (TransferCache.Reserved(relative) || matcher.IsIgnored(relative, true) || matcher.IsIgnored(relative + "/", true)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); } catch (DirectoryNotFoundException) { continue; } catch (FileNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) == 0) yield return relative;
            }
        }

        private static IEnumerable<string> ChangedFiles(string root, string relative)
        {
            var path = SafeFiles.Join(root, relative);
            // Exists() suppresses access errors; GetAttributes distinguishes inaccessible from missing.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new string[0]; }
            catch (DirectoryNotFoundException) { return new string[0]; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new string[0];
            return (attributes & FileAttributes.Directory) != 0 ? SafeFiles.Enumerate(path) : new[] { path };
        }

        private sealed class IgnoreMatcher
        {
            private readonly List<string> patterns = new List<string>();

            public IgnoreMatcher(JArray custom)
            {
                patterns.AddRange(DefaultPatterns);
                if (custom != null)
                {
                    foreach (var token in custom)
                    {
                        var value = token.ToString().Trim();
                        if (!string.IsNullOrEmpty(value)) patterns.Add(value);
                    }
                }
            }

            public bool IsIgnored(string relative, bool directory)
            {
                var path = relative.Replace('\\', '/').ToLowerInvariant();
                var name = Path.GetFileName(path);
                foreach (var pattern in patterns)
                {
                    var clean = pattern.Replace('\\', '/').ToLowerInvariant();
                    if (clean.StartsWith("!")) continue;
                    if (clean.IndexOf('/') < 0)
                    {
                        if (Regex.IsMatch(name, Glob(clean))) return true;
                    }
                    else if (Regex.IsMatch(path, Glob(clean))) return true;
                }
                return false;
            }

            private static string Glob(string pattern)
            {
                var text = Regex.Escape(pattern)
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]");
                return "^" + text + "$";
            }
        }
    }
}
