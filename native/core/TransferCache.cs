using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class TransferCache
    {
        internal const string Name = ".twinsync-cache";
        private const string Marker = "TwinSync transfer cache v1";
        private static readonly object gate = new object();
        internal static bool Reserved(string path) { return (path ?? "").Replace('\\', '/').Split('/').Any(p => p.Equals(Name, StringComparison.OrdinalIgnoreCase)); }
        internal static bool Owned(string directory)
        {
            if (!Path.GetFileName(directory.TrimEnd('\\', '/')).Equals(Name, StringComparison.OrdinalIgnoreCase)) return false;
            try { var marker = SafeFiles.Join(directory, "owner.txt"); return File.Exists(marker) && new FileInfo(marker).Length < 128 && File.ReadAllText(marker, Encoding.UTF8) == Marker; }
            catch { return false; }
        }
        internal static string Ensure(string root)
        {
            lock (gate)
            {
                var directory = SafeFiles.Join(root, Name);
                if (!Owned(directory))
                {
                    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("同步目录中的 .twinsync-cache 已被其他内容占用，未修改其中的文件。");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(SafeFiles.Join(directory, "owner.txt"), Marker, new UTF8Encoding(false));
                }
                File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
                return directory;
            }
        }
        // Compact filenames leave room for the cache directory on Windows; the complete identity remains in metadata.
        private static string Compact(string id) { return id.Substring(0, Math.Min(48, id.Length)); }
        internal static string Upload(string root, string id) { return SafeFiles.Join(Ensure(root), "upload-" + Compact(id) + ".part"); }
        internal static string ExistingUpload(string root, string id)
        {
            var directory = SafeFiles.Join(root, Name);
            var part = SafeFiles.Join(directory, "upload-" + Compact(id) + ".part");
            return Owned(directory) && File.Exists(part) ? part : SafeFiles.Join(root, ".twinsync-part-" + id);
        }
        internal static string RecoverUpload(string root, string id)
        {
            var part = Upload(root, id); var legacy = SafeFiles.Join(root, ".twinsync-part-" + id);
            if (!File.Exists(part) && File.Exists(legacy)) File.Move(legacy, part);
            return part;
        }
        internal static string Download(string root, string destination, string key, string hash)
        {
            lock (gate)
            {
            var boundary = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(destination);
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载缓存超出同步目录。");
            var relative = target.Substring(boundary.Length).Replace('\\', '/'); SafeFiles.UserPath(root, relative);
            var directory = Ensure(root); var part = SafeFiles.Join(directory, "download-" + Compact(key) + ".part");
            var legacy = destination + ".twinsync-part-" + key;
            if (!File.Exists(part) && File.Exists(legacy)) File.Move(legacy, part);
            var metaPath = Path.ChangeExtension(part, ".json");
            var prior = JsonUtil.LoadObject(metaPath);
            if (prior != null && (string)prior["key"] != key) throw new IOException("下载缓存标识冲突，已保留原缓存。");
            SaveMeta(metaPath, JsonUtil.Obj("key", key, "path", relative, "hash", hash));
            return part;
            }
        }
        private static void SaveMeta(string path, JObject value)
        {
            // Using a separate short name avoids appending a GUID to an already long metadata filename.
            var temporary = SafeFiles.Join(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, value.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static void ForgetDownload(string part)
        {
            if (!Regex.IsMatch(Path.GetFileName(part), "^download-[a-f0-9]{48}\\.part$") || !Owned(Path.GetDirectoryName(part))) return;
            if (!File.Exists(part)) { var meta = Path.ChangeExtension(part, ".json"); if (File.Exists(meta)) File.Delete(meta); }
        }
        internal static void MigrateOrphans(string root, string[] liveIds)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, ".twinsync-part-*", SearchOption.TopDirectoryOnly))
            {
                var match = Regex.Match(Path.GetFileName(file), "^\\.twinsync-part-([a-f0-9]{32}|[a-f0-9]{64})$");
                if (!match.Success || liveIds.Contains(match.Groups[1].Value)) continue;
                try
                {
                    SafeFiles.Join(root, Path.GetFileName(file));
                    if (new FileInfo(file).Length == 0) { File.Delete(file); continue; }
                    var destination = Orphan(root, match.Groups[1].Value);
                    if (File.Exists(destination)) continue;
                    File.Move(file, destination); File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        internal static void Maintain(string root, IndexStore index)
        {
            lock (gate)
            {
            var directory = SafeFiles.Join(root, Name); if (!Owned(directory)) return;
            foreach (var file in Directory.GetFiles(directory, "download-*.json"))
            {
                if (!Regex.IsMatch(Path.GetFileName(file), "^download-[a-f0-9]{48}\\.json$")) continue;
                try
                {
                    if (new FileInfo(file).Length > 16384) continue;
                    var meta = JsonUtil.LoadObject(file); var part = Path.ChangeExtension(file, ".part");
                    if (!File.Exists(part)) { File.Delete(file); continue; }
                    var target = SafeFiles.UserPath(root, (string)meta["path"]); var current = index.Get((string)meta["path"]);
                    var expired = DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > TimeSpan.FromDays(7);
                    var completed = current != null && !EntryUtil.Deleted(current) && EntryUtil.HashOf(current) == (string)meta["hash"] && File.Exists(target);
                    var empty = new FileInfo(part).Length == 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > TimeSpan.FromMinutes(2);
                    if (!completed && !expired && !empty) continue;
                    // An active download holds FileShare.None, so an in-progress file is never reclaimed.
                    using (var held = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    File.Delete(part); File.Delete(file);
                }
                catch { }
            }
            foreach (var file in Directory.GetFiles(directory, "orphan-*.part"))
                if (Regex.IsMatch(Path.GetFileName(file), "^orphan-(?:[a-f0-9]{32}|[a-f0-9]{48})\\.part$") && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
                    try { using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { } File.Delete(file); } catch { }
            foreach (var file in Directory.GetFiles(directory, "download-*.part"))
                if (Regex.IsMatch(Path.GetFileName(file), "^download-[a-f0-9]{48}\\.part$") && !File.Exists(Path.ChangeExtension(file, ".json")) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
                    try { using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { } File.Delete(file); } catch { }
            }
        }
        internal static string Orphan(string root, string id) { return SafeFiles.Join(Ensure(root), "orphan-" + Compact(id) + ".part"); }
    }
}
