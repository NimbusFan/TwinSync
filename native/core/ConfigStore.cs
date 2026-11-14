using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class ConfigStore
    {
        public string Home { get; private set; }
        public string ConfigPath { get; private set; }
        public string StateDir { get; private set; }
        public string VersionsDir { get; private set; }
        public string LogsDir { get; private set; }
        public JObject Root { get; private set; }
        private readonly object gate = new object();

        public ConfigStore(string home)
        {
            Home = home;
            ConfigPath = Path.Combine(home, "config.json");
            StateDir = Path.Combine(home, "state");
            VersionsDir = Path.Combine(home, "versions");
            LogsDir = Path.Combine(home, "logs");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(VersionsDir);
            Directory.CreateDirectory(LogsDir);
            Load();
        }

        private void Load()
        {
            Root = JsonUtil.LoadObject(ConfigPath);
            if (Root == null)
            {
                Root = CreateDefault();
                Save();
            }
            if (Root["device"] == null) Root["device"] = new JObject();
            if (Root["settings"] == null) Root["settings"] = new JObject();
            if (!(Root["peers"] is JArray)) Root["peers"] = new JArray();
            if (!(Root["folders"] is JArray)) Root["folders"] = new JArray();
            if (Root["meta"] == null) Root["meta"] = new JObject();
            Device["secret"] = DeviceSecurity.Unprotect(JsonUtil.GetString(Device["secret"], ""));
            foreach (var peer in Peers.OfType<JObject>()) peer["token"] = DeviceSecurity.Unprotect(JsonUtil.GetString(peer["token"], ""));
            Save();
        }

        private JObject CreateDefault()
        {
            var device = JsonUtil.Obj(
                "id", JsonUtil.RandomId(),
                "name", "",
                "nameConfigured", false,
                "hostname", Environment.MachineName,
                "platform", "win32",
                "port", 55220,
                "createdAt", JsonUtil.NowIso(),
                "secret", JsonUtil.RandomToken(32));
            var settings = JsonUtil.Obj(
                "theme", "light",
                "language", "zh-CN",
                "autoSync", true,
                "syncPaused", false,
                "syncIntervalSec", 30,
                "conflictPolicy", "copy",
                "versioningEnabled", true,
                "maxVersionsPerFile", 10,
                "versionRetentionDays", 30,
                "discoveryEnabled", true,
                "notifyOnComplete", true,
                "openAtLogin", false,
                "allowRemoteUi", false,
                "pauseOnMetered", false);
            return JsonUtil.Obj(
                "version", 1,
                "device", device,
                "settings", settings,
                "peers", new JArray(),
                "folders", new JArray(),
                "meta", JsonUtil.Obj("createdAt", JsonUtil.NowIso()));
        }

        public void Save()
        {
            lock (gate)
            {
                var persisted = (JObject)Root.DeepClone();
                persisted["device"]["secret"] = DeviceSecurity.Protect(JsonUtil.GetString(Device["secret"], ""));
                foreach (var peer in ((JArray)persisted["peers"]).OfType<JObject>()) peer["token"] = DeviceSecurity.Protect(JsonUtil.GetString(peer["token"], ""));
                JsonUtil.SaveAtomic(ConfigPath, persisted);
            }
        }

        public JObject Device { get { return (JObject)Root["device"]; } }
        public JObject Settings { get { return (JObject)Root["settings"]; } }
        public JArray Peers { get { return (JArray)Root["peers"]; } }
        public JArray Folders { get { return (JArray)Root["folders"]; } }
        public string DeviceId { get { return JsonUtil.GetString(Device["id"], ""); } }
        public string DeviceName { get { return JsonUtil.GetString(Device["name"], "未命名设备"); } }
        public int Port { get { return JsonUtil.GetInt(Device["port"], 55220); } }

        public JObject GetFolder(string id)
        {
            return Folders.OfType<JObject>().FirstOrDefault(folder => JsonUtil.GetString(folder["id"], "") == id);
        }

        public JObject GetPeer(string id)
        {
            return Peers.OfType<JObject>().FirstOrDefault(peer => JsonUtil.GetString(peer["id"], "") == id);
        }

        public int VersionCount()
        {
            var file = Path.Combine(VersionsDir, "index.json");
            if (!File.Exists(file)) return 0;
            try { return JsonUtil.LoadArray(file).Count; } catch { return 0; }
        }

        public List<object> Addresses()
        {
            var result = new List<object>();
            try
            {
                var port = Port;
                var seen = new HashSet<string>();
                var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback && item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderBy(item => item.GetIPProperties().GatewayAddresses.Count == 0 ? 1 : 0);
                foreach (var network in interfaces)
                {
                    foreach (var address in network.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var ip = address.Address.ToString();
                        if (IPAddress.IsLoopback(address.Address) || ip.StartsWith("169.254.") || !seen.Add(ip)) continue;
                        result.Add(new { address = ip, name = network.Name, url = "https://" + ip + ":" + port, recommended = network.GetIPProperties().GatewayAddresses.Count > 0 });
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
