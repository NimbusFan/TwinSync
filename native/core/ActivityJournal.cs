using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class ActivityJournal
    {
        private readonly object gate = new object();
        private readonly ConfigStore config;
        private readonly string file;
        private readonly int capacity;
        private readonly JArray recent = new JArray();
        internal string Error { get; private set; }
        internal ActivityJournal(ConfigStore config, int capacity = 524288)
        {
            this.config = config; this.capacity = capacity; file = Path.Combine(config.LogsDir, "activity.jsonl");
            for (var i = 3; i >= 0; i--)
            {
                var path = i == 0 ? file : file + "." + i;
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > capacity) continue;
                    foreach (var line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (line.Length > 16384) continue;
                        try { recent.Insert(0, Clean(JObject.Parse(line))); while (recent.Count > 300) recent.RemoveAt(300); } catch { }
                    }
                }
                catch (Exception error) { Error = error.Message; }
            }
        }
        internal string Redact(string text)
        {
            text = text ?? "";
            foreach (var secret in config.Peers.OfType<JObject>().Select(p => (string)p["token"]).Concat(new[] { (string)config.Device["secret"] }).Where(s => !string.IsNullOrEmpty(s))) text = text.Replace(secret, "[密钥]");
            text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\""<>|]*", "[本地路径]");
            text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[地址]");
            text = Regex.Replace(text, @"(?i)(?<![\w:])(?:(?:[a-f0-9]{1,4}:){7}[a-f0-9]{1,4}|[a-f0-9:]*::[a-f0-9:.%]*)", "[地址]");
            text = Regex.Replace(text, @"(?i)\b(?:[a-f0-9]{64}|[a-f0-9]{8}-[a-f0-9-]{27,})\b", "[标识]");
            return text.Length > 1024 ? text.Substring(0, 1024) : text;
        }
        private JObject Clean(JObject item)
        {
            var result = JsonUtil.Obj("id", JsonUtil.GetString(item["id"], JsonUtil.RandomId()), "at", Redact((string)item["at"]), "type", Redact((string)item["type"]), "title", Redact((string)item["title"]), "detail", Redact((string)item["detail"]), "level", Redact((string)item["level"]));
            var data = item["data"] as JObject;
            if (data != null)
            {
                var clean = new JObject();
                foreach (var key in new[] { "actor", "folder", "path", "kind", "action", "direction", "scope", "files", "bytes", "durationMs", "deleted", "checked", "uploaded", "downloaded", "failed" })
                {
                    var value = data[key]; if (value == null) continue;
                    if (value.Type == JTokenType.String) clean[key] = Redact((string)value);
                    else if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) clean[key] = Math.Max(0, value.Value<double>());
                }
                result["data"] = clean;
            }
            return result;
        }
        internal JObject Append(string type, string title, string detail, string level, JObject data = null)
        {
            lock (gate)
            {
                var item = Clean(JsonUtil.Obj("id", JsonUtil.RandomId(), "at", JsonUtil.NowIso(), "type", type, "title", title, "detail", detail, "level", level, "data", data));
                recent.Insert(0, item); while (recent.Count > 300) recent.RemoveAt(300);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(item.ToString(Formatting.None) + "\n");
                    if (File.Exists(file) && new FileInfo(file).Length + bytes.Length > capacity)
                    {
                        if (File.Exists(file + ".3")) File.Delete(file + ".3");
                        for (var i = 2; i >= 0; i--) { var source = i == 0 ? file : file + "." + i; if (File.Exists(source)) File.Move(source, file + "." + (i + 1)); }
                    }
                    using (var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                    Error = null;
                }
                catch (Exception error) { Error = error.Message; }
                return (JObject)item.DeepClone();
            }
        }
        internal JArray Read(int limit) { lock (gate) return new JArray(recent.Take(Math.Max(1, Math.Min(limit, 300))).Select(v => v.DeepClone())); }
        internal JObject Export(JObject status)
        {
            // Deliberate whitelist: no configuration, paths, file lists, names, certificates or credentials.
            var events = Read(300);
            foreach (var item in events.OfType<JObject>())
            {
                if (item["data"] != null) { item.Remove("data"); item["title"] = (string)item["type"] == "file" ? "文件变更" : (string)item["type"] == "device" ? "设备动态" : "同步记录"; }
                foreach (var prop in new[] { "title", "detail" })
                {
                    var text = Redact((string)item[prop]);
                    foreach (var name in config.Peers.OfType<JObject>().Select(p => (string)p["name"]).Concat(config.Folders.OfType<JObject>().Select(f => (string)f["name"])).Concat(new[] { config.DeviceName, Environment.UserName, Environment.MachineName }).Where(n => !string.IsNullOrWhiteSpace(n)).OrderByDescending(n => n.Length)) text = text.Replace(name, "[名称]");
                    item[prop] = text;
                }
                // Error messages can contain arbitrary user filenames. The exported timeline retains categories and outcomes only.
                item["detail"] = (string)item["type"] == "version" && ((string)item["detail"] ?? "").StartsWith("成功 ") ? item["detail"] : JValue.CreateNull();
            }
            return JsonUtil.Obj("format", 1, "createdAt", JsonUtil.NowIso(), "runtime", Environment.Version.ToString(), "os", Environment.OSVersion.Version.ToString(), "folders", config.Folders.Count, "peers", config.Peers.Count, "sync", JsonUtil.Obj("state", status["state"], "lastResult", status["lastResult"], "summary", status["summary"] == null ? null : status["summary"].DeepClone()), "journalAvailable", Error == null, "activity", events);
        }
    }
}
