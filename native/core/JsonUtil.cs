using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class JsonUtil
    {
        public static string NowIso()
        {
            return DateTime.UtcNow.ToString("o");
        }

        public static string RandomId()
        {
            return Guid.NewGuid().ToString();
        }

        public static string RandomToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buffer);
            }
            return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static JObject LoadObject(string file)
        {
            if (!File.Exists(file)) return null;
            return JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
        }

        public static JArray LoadArray(string file)
        {
            if (!File.Exists(file)) return new JArray();
            var token = JToken.Parse(File.ReadAllText(file, Encoding.UTF8));
            return token as JArray ?? new JArray();
        }

        public static void SaveAtomic(string file, JToken token)
        {
            SaveAtomic(file, delegate(JsonTextWriter writer) { token.WriteTo(writer); });
        }

        public static void SaveAtomic(string file, Action<JsonTextWriter> write)
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new StreamWriter(temp, false, new UTF8Encoding(false)))
                using (var writer = new JsonTextWriter(stream))
                {
                    writer.Formatting = Formatting.Indented;
                    write(writer);
                }
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static string GetString(JToken token, string fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            var value = token.ToString();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        public static bool GetBool(JToken token, bool fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.Value<bool>(); } catch { return fallback; }
        }

        public static int GetInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.Value<int>(); } catch { return fallback; }
        }

        public static long GetLong(JToken token, long fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.Value<long>(); } catch { return fallback; }
        }

        public static JObject Obj(params object[] pairs)
        {
            var result = new JObject();
            for (var index = 0; index + 1 < pairs.Length; index += 2)
            {
                var key = Convert.ToString(pairs[index]);
                var value = pairs[index + 1];
                if (value == null) result[key] = JValue.CreateNull();
                else if (value is JToken) result[key] = (JToken)value;
                else result[key] = JToken.FromObject(value);
            }
            return result;
        }

        public static string Sha256File(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                var hash = sha.ComputeHash(stream);
                var text = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }
    }
}
