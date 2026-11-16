using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class DeviceSecurity
    {
        public X509Certificate2 Certificate { get; private set; }
        public string Fingerprint { get { return FingerprintOf(Certificate); } }
        public string CsrfToken = JsonUtil.RandomToken(32);
        public string RemoteToken = JsonUtil.RandomToken(32);

        public DeviceSecurity(string home)
        {
            var file = Path.Combine(home, "identity.dpapi");
            if (File.Exists(file))
            {
                Certificate = new X509Certificate2(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            else
            {
                using (var rsa = new RSACryptoServiceProvider(2048))
                {
                    rsa.PersistKeyInCsp = false;
                    var request = new CertificateRequest("CN=TwinSync", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    using (var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)))
                    {
                        var bytes = created.Export(X509ContentType.Pfx, "");
                        File.WriteAllBytes(file, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                        Certificate = new X509Certificate2(bytes, "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    }
                }
            }
        }

        public static string FingerprintOf(X509Certificate certificate)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", "");
        }

        public static string NormalizeFingerprint(string value)
        {
            var normalized = Regex.Replace(value ?? "", "[\\s:-]", "").ToUpperInvariant();
            if (!Regex.IsMatch(normalized, "^[0-9A-F]{64}$")) throw new Exception("请从对方设备页复制完整的 SHA-256 设备指纹");
            return normalized;
        }

        public static bool Equal(string left, string right)
        {
            var a = Encoding.UTF8.GetBytes(left ?? "");
            var b = Encoding.UTF8.GetBytes(right ?? "");
            var difference = a.Length ^ b.Length;
            for (var index = 0; index < Math.Max(a.Length, b.Length); index++) difference |= (index < a.Length ? a[index] : 0) ^ (index < b.Length ? b[index] : 0);
            return difference == 0;
        }

        public static string Protect(string value)
        {
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string value)
        {
            if (!value.StartsWith("dpapi:", StringComparison.Ordinal)) return value;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(6)), null, DataProtectionScope.CurrentUser));
        }

        public static JObject PublicDevice(JObject device)
        {
            var result = (JObject)device.DeepClone();
            result.Remove("secret");
            return result;
        }
    }

    internal static class SafeFiles
    {
        internal static string UserPath(string root, string relative)
        {
            if (TransferCache.Reserved(relative) || TransferCache.Reserved(root)) throw new IOException("传输缓存目录不属于可共享的文件内容。");
            return Join(root, relative);
        }
        public static string Join(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw new Exception("请选择本机的绝对目录");
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            relative = (relative ?? "").Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0) throw new Exception("非法文件路径");
            if (relative.Length > 0)
            {
                foreach (var part in relative.Split('/'))
                {
                    if (part.Length == 0 || part == "." || part == ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) throw new Exception("非法文件路径");
                }
            }
            var target = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (target != basePath.TrimEnd(Path.DirectorySeparatorChar) && !target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new Exception("路径超出共享目录");
            for (var current = target; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new Exception("同步目录不能经过符号链接或目录联接");
            }
            return target;
        }

        public static IEnumerable<string> Enumerate(string root, Action<string> visitDirectory = null)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (visitDirectory != null) visitDirectory(directory);
                string[] children;
                try { children = Directory.GetFileSystemEntries(directory); }
                catch (DirectoryNotFoundException) { continue; }
                foreach (var child in children)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { if (!TransferCache.Owned(child)) pending.Push(child); }
                    else yield return child;
                }
            }
        }
    }
}
