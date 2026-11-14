using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TwinSync.Native
{
    internal static class FolderMigration
    {
        internal static string Normalize(string path) { return Path.GetFullPath(path).TrimEnd('\\', '/') + "\\"; }

        internal static void CopyAndVerify(string source, string destination, IndexStore index)
        {
            var oldRoot = Normalize(source); var newRoot = Normalize(destination);
            if (oldRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (oldRoot.StartsWith(newRoot, StringComparison.OrdinalIgnoreCase) || newRoot.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("新旧同步目录不能互相包含，请选择独立的文件夹。");
            SafeFiles.Join(destination, "");
            var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(source))
            {
                SafeFiles.Join(source, "");
                foreach (var file in SafeFiles.Enumerate(source))
                {
                    if (Path.GetFileName(file).IndexOf(".twinsync-part-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var relative = file.Substring(oldRoot.Length).Replace('\\', '/');
                    var target = SafeFiles.Join(destination, relative);
                    if (Directory.Exists(target)) throw new Exception("新位置已有同名文件夹：" + relative);
                    var hash = JsonUtil.Sha256File(file);
                    if (File.Exists(target) && JsonUtil.Sha256File(target) != hash) throw new Exception("新位置存在同名但内容不同的文件，未覆盖：" + relative + "。若要同步另一组文件，请另行添加同步文件夹。");
                    copies[relative] = hash;
                }
            }
            foreach (var entry in index.Entries.Values.Where(entry => !EntryUtil.Deleted(entry)))
            {
                var relative = EntryUtil.PathOf(entry);
                if (!copies.ContainsKey(relative) && !File.Exists(SafeFiles.Join(destination, relative))) throw new Exception("迁移缺少已记录的文件：" + relative + "。请先将原文件复制到新位置，再更换目录。");
            }
            Directory.CreateDirectory(destination);
            foreach (var copy in copies)
            {
                var target = SafeFiles.Join(destination, copy.Key);
                if (File.Exists(target)) continue;
                var sourceFile = SafeFiles.Join(source, copy.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var temporary = target + ".twinsync-part-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(sourceFile, temporary, false);
                    if (JsonUtil.Sha256File(temporary) != copy.Value || JsonUtil.Sha256File(sourceFile) != copy.Value) throw new Exception("迁移期间文件发生变化，请重新保存：" + copy.Key);
                    File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(sourceFile));
                    SafeFiles.Join(destination, copy.Key);
                    File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (var entry in index.Entries.Values.Where(entry => !EntryUtil.Deleted(entry)))
                if (!File.Exists(SafeFiles.Join(destination, EntryUtil.PathOf(entry)))) throw new Exception("新目录尚未包含全部已记录文件，已保留原目录设置。");
        }
    }
}
