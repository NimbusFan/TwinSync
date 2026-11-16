using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json.Linq;
namespace TwinSync.Native
{
    internal static class WebFixture
    {
        public static int Main(string[] args)
        {
            CoreService core = null;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var stage = args[1]; core = new CoreService(args[0], Path.Combine(stage, "home"), port, path => { }, enabled => { });
                core.Config.Settings["autoSync"] = false; core.Config.Settings["discoveryEnabled"] = false; core.Config.Settings["theme"] = "dark";
                core.Config.Device["name"] = "本机测试"; core.Config.Device["port"] = port;
                var peer = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "对方测试设备", "host", "127.0.0.1", "port", 1, "token", JsonUtil.RandomToken(32), "fingerprint", new string('A', 64), "autoSync", false);
                core.Config.Peers.Add(peer);
                var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "已添加的文件夹", "path", Path.Combine(stage, "content"), "mode", "two-way", "peerIds", new JArray(peer["id"]), "enabled", true);
                Directory.CreateDirectory((string)folder["path"]); File.WriteAllText(Path.Combine((string)folder["path"], "example.txt"), "test data"); core.Config.Folders.Add(folder);
                core.Engine.MergeFolders(new JArray(JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "待确认邀请")), (string)peer["id"]);
                core.Engine.ScanFolder(folder, true); core.Config.Save();
                core.Engine.AddActivity("sync", "可见的同步记录", "API 契约回归", "success"); core.Engine.AddActivity("file", "可见的文件记录", "API 契约回归", "info");
                core.Start(); JsonUtil.SaveAtomic(Path.Combine(stage, "ready.json"), JsonUtil.Obj("port", port));
                var churn = 0;
                while (!File.Exists(Path.Combine(stage, "stop")))
                {
                    var signal = Path.Combine(stage, "add-offer");
                    if (File.Exists(signal)) { File.Delete(signal); lock (core.Engine.ConfigurationGate) core.Engine.MergeFolders(new JArray(JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "新收到的邀请")), (string)peer["id"]); }
                    if (File.Exists(Path.Combine(stage, "churn")) && ++churn % 4 == 0)
                    {
                        var added = Path.Combine((string)folder["path"], "new-directory");
                        Directory.CreateDirectory(added);
                        File.WriteAllText(Path.Combine(added, "new-file.txt"), churn.ToString());
                        core.Engine.ScanFolder(folder, true);
                        lock (core.Engine.ConfigurationGate) core.Engine.SetStatus("syncing", "正在同步 " + churn, "new-directory/new-file.txt");
                        core.Engine.AddActivity("file", "文件变更 " + churn, "", "info");
                    }
                    Thread.Sleep(50);
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (core != null) core.Stop(); }
        }
    }
}
