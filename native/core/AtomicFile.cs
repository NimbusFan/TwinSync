using System;
using System.IO;
using System.Security.Cryptography;

namespace TwinSync.Native
{
    internal static class AtomicFile
    {
        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string HashOrNull(string path) { return File.Exists(path) ? JsonUtil.Sha256File(path) : null; }
        internal static void AssertUnchanged(string target, string expected) { using (var current = OpenExpected(target, expected)) { } }

        private static FileStream OpenExpected(string target, string expected)
        {
            if (expected == null)
            {
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("本地路径已有新内容，未覆盖：" + Path.GetFileName(target));
                return null;
            }
            FileStream current = null;
            try
            {
                // Deny ordinary writers while checking and replacing; allow the atomic replacement itself.
                current = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (!string.Equals(Hash(current), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("本地文件在同步期间发生变化，已保留，请重新核对：" + Path.GetFileName(target));
                return current;
            }
            catch { if (current != null) current.Dispose(); throw; }
        }

        internal static void Commit(string temporary, string target, string incomingHash, string expectedLocalHash, double mtime, Action check)
        {
            try
            {
                if (!string.Equals(JsonUtil.Sha256File(temporary), incomingHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("待写入文件校验失败。");
                using (var durable = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
                if (mtime > 0) File.SetLastWriteTimeUtc(temporary, DateTimeOffset.FromUnixTimeMilliseconds((long)mtime).UtcDateTime);
                if (check != null) check();
                using (var current = OpenExpected(target, expectedLocalHash))
                {
                    if (check != null) check();
                    if (current == null) File.Move(temporary, target);
                    else File.Replace(temporary, target, null);
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal static void CopyVerified(string source, string target, string hash, string expectedLocalHash, double mtime, Action check)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + ".twinsync-part-" + Guid.NewGuid().ToString("N");
            try { File.Copy(source, temporary, false); Commit(temporary, target, hash, expectedLocalHash, mtime, check); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal static void DeleteUnchanged(string target, string expectedLocalHash, Action check)
        {
            if (check != null) check();
            using (var current = OpenExpected(target, expectedLocalHash))
            {
                if (check != null) check();
                if (current != null) File.Delete(target);
            }
        }
    }
}
