using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace TwinSync.Setup
{
    internal interface IInstallIntegration
    {
        void Register(string directory, bool desktopShortcut);
        void Unregister(string directory);
    }

    internal static class InstallEngine
    {
        internal const string ManifestName = "twinsync-install.xml";
        internal static string DefaultDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TwinSync"); } }

        internal static string ValidateDirectory(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new Exception("请选择安装位置。");
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')) throw new Exception("请选择本机磁盘上的完整路径。");
            var directory = Path.GetFullPath(value).TrimEnd('\\', '/');
            if (string.Equals(directory, Path.GetPathRoot(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new Exception("请使用独立的应用文件夹，例如 D:\\Apps\\TwinSync。");
            if (File.Exists(directory)) throw new Exception("这个位置是文件，请选择一个文件夹。");
            CheckLinks(directory);
            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length > 0 && !Recognized(directory)) throw new Exception("这个文件夹已有其他内容，请选择空文件夹或已有的 TwinSync 安装目录。");
            return directory;
        }

        internal static bool Recognized(string directory)
        {
            var manifest = Path.Combine(directory, ManifestName);
            if (File.Exists(manifest)) { try { return ReadManifest(directory).DocumentElement.GetAttribute("product") == "TwinSync"; } catch { return false; } }
            var legacy = Path.Combine(directory, "installed.txt");
            return File.Exists(legacy) && File.ReadAllText(legacy).StartsWith("TwinSync installed at ", StringComparison.Ordinal) && File.Exists(Path.Combine(directory, "TwinSync.exe"));
        }

        internal static string BoundedPath(string directory, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.Replace('\\', '/').Split('/').Any(part => part == ".." || part == "." || part.Length == 0 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))) throw new Exception("安装包包含无效路径。");
            var boundary = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            var target = Path.GetFullPath(Path.Combine(boundary, relative.Replace('/', '\\')));
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new Exception("安装文件超出了目标目录。");
            CheckLinks(target);
            return target;
        }

        private static void CheckLinks(string path)
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new Exception("安装位置不能经过符号链接或目录联接。");
        }

        internal static void EnsureNotRunning(string directory)
        {
            var executable = Path.Combine(Path.GetFullPath(directory), "TwinSync.exe");
            foreach (var process in Process.GetProcessesByName("TwinSync"))
            {
                using (process)
                {
                    string running;
                    try { running = process.MainModule.FileName; } catch { continue; }
                    if (string.Equals(running, executable, StringComparison.OrdinalIgnoreCase)) throw new Exception("请先从托盘退出正在运行的 TwinSync，再继续安装或卸载。");
                }
            }
        }

        internal static void Install(string directory, bool desktopShortcut, string previous, IInstallIntegration integration, Action<int, string> report, Stream suppliedPayload = null)
        {
            directory = ValidateDirectory(directory);
            EnsureNotRunning(directory);
            if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, directory, StringComparison.OrdinalIgnoreCase))
            {
                var oldData = Path.GetFullPath(Path.Combine(previous, "data")).TrimEnd('\\');
                if (directory.Equals(oldData, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(oldData + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("新安装位置不能位于原来的数据目录中。");
                EnsureNotRunning(previous);
            }
            var work = Path.Combine(Path.GetTempPath(), "TwinSyncSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                report(5, "正在检查安装文件…");
                var payload = Path.Combine(work, "payload.zip");
                using (var input = suppliedPayload ?? Assembly.GetExecutingAssembly().GetManifestResourceStream("TwinSyncPayload"))
                {
                    if (input == null) throw new Exception("安装包缺少应用文件。");
                    using (var output = File.Create(payload)) input.CopyTo(output);
                }
                var staged = Path.Combine(work, "app");
                Directory.CreateDirectory(staged);
                using (var zip = ZipFile.OpenRead(payload))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var relative = entry.FullName.Replace('\\', '/');
                        if (relative.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || relative.Equals("data", StringComparison.OrdinalIgnoreCase) || relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) throw new Exception("安装包不能包含用户数据或外部安装记录。");
                        var target = BoundedPath(staged, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target);
                    }
                }
                if (!File.Exists(Path.Combine(staged, "TwinSync.exe")) || !File.Exists(Path.Combine(staged, "public", "index.html"))) throw new Exception("安装文件不完整。");
                report(20, "正在安装应用…");
                Directory.CreateDirectory(directory);
                // Fail early for unwritable destinations without requesting elevation.
                var probe = Path.Combine(directory, ".twinsync-write-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { } File.Delete(probe);
                var files = Directory.GetFiles(staged, "*", SearchOption.AllDirectories);
                var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Keep a valid record even if a copy is interrupted, so the user can retry.
                if (File.Exists(Path.Combine(directory, ManifestName)))
                    foreach (XmlElement old in ReadManifest(directory).SelectNodes("/installation/file"))
                    {
                        var relative = old.GetAttribute("path");
                        BoundedPath(directory, relative);
                        installed[relative] = old.GetAttribute("hash");
                    }
                foreach (var file in files) installed[file.Substring(staged.Length + 1).Replace('\\', '/')] = Hash(file);
                WriteManifest(directory, installed, "installing");
                for (var index = 0; index < files.Length; index++)
                {
                    var relative = files[index].Substring(staged.Length + 1).Replace('\\', '/');
                    var target = BoundedPath(directory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(files[index], target, true);
                    report(20 + (int)(55d * (index + 1) / files.Length), "正在安装应用…");
                }
                if (!string.IsNullOrEmpty(previous) && !string.Equals(Path.GetFullPath(previous), directory, StringComparison.OrdinalIgnoreCase) && Recognized(previous))
                {
                    var oldData = Path.Combine(previous, "data");
                    var newData = BoundedPath(directory, "data");
                    if (Directory.Exists(oldData) && !Directory.Exists(newData))
                    {
                        report(80, "正在保留原有配置…");
                        var migration = BoundedPath(directory, ".twinsync-migrate-" + Guid.NewGuid().ToString("N"));
                        try { CopyData(oldData, migration); Directory.Move(migration, newData); }
                        finally { if (Directory.Exists(migration)) Directory.Delete(migration, true); }
                    }
                }
                WriteManifest(directory, installed);
                report(90, "正在创建快捷方式…");
                integration.Register(directory, desktopShortcut);
                report(100, "安装完成");
            }
            catch (UnauthorizedAccessException) { throw new Exception("这个位置不可写，请选择有写入权限的文件夹。"); }
            finally
            {
                var boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(work).StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(work).StartsWith("TwinSyncSetup-", StringComparison.Ordinal)) Directory.Delete(work, true);
            }
        }

        private static void CopyData(string source, string target)
        {
            CheckLinks(source); Directory.CreateDirectory(target);
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                CheckLinks(entry);
                var destination = Path.Combine(target, Path.GetFileName(entry));
                if (Directory.Exists(entry)) CopyData(entry, destination);
                else File.Copy(entry, destination, false);
            }
        }

        private static string Hash(string file) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(input)); }

        private static void WriteManifest(string directory, Dictionary<string, string> files, string state = "installed")
        {
            var destination = Path.Combine(directory, ManifestName);
            var temporary = Path.Combine(directory, ".twinsync-record-" + Guid.NewGuid().ToString("N"));
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("installation"); writer.WriteAttributeString("product", "TwinSync"); writer.WriteAttributeString("directory", directory); writer.WriteAttributeString("state", state);
                foreach (var file in files) { writer.WriteStartElement("file"); writer.WriteAttributeString("path", file.Key); writer.WriteAttributeString("hash", file.Value); writer.WriteEndElement(); }
                writer.WriteEndElement();
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }

        private static XmlDocument ReadManifest(string directory)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(Path.Combine(directory, ManifestName), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "installation" || document.DocumentElement.GetAttribute("product") != "TwinSync" || !string.Equals(Path.GetFullPath(document.DocumentElement.GetAttribute("directory")), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) throw new Exception("安装记录与所选目录不匹配。");
            return document;
        }

        internal static void Uninstall(string directory, IInstallIntegration integration)
        {
            directory = Path.GetFullPath(directory); CheckLinks(directory); EnsureNotRunning(directory);
            var manifest = ReadManifest(directory);
            // Validate the complete manifest before any removal; preserve data and unrelated or modified files.
            var targets = new List<string>();
            foreach (XmlElement node in manifest.SelectNodes("/installation/file"))
            {
                var relative = node.GetAttribute("path").Replace('\\', '/');
                if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || relative.Equals("data", StringComparison.OrdinalIgnoreCase)) throw new Exception("安装记录包含用户数据，已停止卸载。");
                var target = BoundedPath(directory, relative);
                if (File.Exists(target) && string.Equals(Hash(target), node.GetAttribute("hash"), StringComparison.Ordinal)) targets.Add(target);
            }
            foreach (var target in targets) File.Delete(target);
            foreach (var parent in targets.Select(Path.GetDirectoryName).Distinct().OrderByDescending(path => path.Length))
            {
                var current = parent;
                while (current.Length > directory.Length && Directory.Exists(current) && Directory.GetFileSystemEntries(current).Length == 0) { Directory.Delete(current, false); current = Path.GetDirectoryName(current); }
            }
            manifest.DocumentElement.SetAttribute("state", "uninstalled"); manifest.Save(Path.Combine(directory, ManifestName));
            integration.Unregister(directory);
        }
    }
}
