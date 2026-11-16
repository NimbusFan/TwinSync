using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static JObject[] FileEvents(CoreService core, string folder)
        {
            core.Engine.FlushActivity(true);
            return core.Engine.Journal.Read(300).OfType<JObject>().Where(item => (string)item["type"] == "file" && item["data"] != null && (string)item["data"]["folder"] == folder).ToArray();
        }
        private static JObject LastSyncEvent(CoreService core)
        { return core.Engine.Journal.Read(300).OfType<JObject>().First(item => (string)item["type"] == "sync" && item["data"] != null); }

        private static void ActivityCases(CoreService a, CoreService b, string root)
        {
            var beforeChecks = checks; var originalA = (string)a.Config.Device["name"]; var originalB = (string)b.Config.Device["name"]; var peerA = (string)a.Config.GetPeer(b.Config.DeviceId)["name"]; var peerB = (string)b.Config.GetPeer(a.Config.DeviceId)["name"];
            a.Config.Device["name"] = "activity-source"; b.Config.Device["name"] = "activity-receiver";
            a.Config.GetPeer(b.Config.DeviceId)["name"] = "activity-receiver"; b.Config.GetPeer(a.Config.DeviceId)["name"] = "activity-source";
            try
            {
                var id = Share(a, b, root, "activity-details");
                a.Engine.ScanFolder(a.Config.GetFolder(id), true); b.Engine.ScanFolder(b.Config.GetFolder(id), true);
                a.Engine.FlushActivity(true); b.Engine.FlushActivity(true);
                var baseline = FileEvents(a, "activity-details").Select(item => (string)item["id"]).ToArray();
                Put(a, id, "one.txt", "single");
                Put(a, id, "photos/a.txt", "aaaa"); Put(a, id, "photos/nested/b.txt", "bbbb");
                Directory.CreateDirectory(FilePath(a, id, "empty"));
                var result = Run(a, id);
                Check((string)result["lastResult"] == "success", "Activity fixture sync failed.");
                var events = FileEvents(a, "activity-details").Where(item => !baseline.Contains((string)item["id"])).ToArray();
                Check(events.Count(item => (string)item["data"]["path"] == "photos" && (string)item["data"]["kind"] == "directory" && (long)item["data"]["files"] == 2 && (long)item["data"]["bytes"] == 8) == 1, "Directory addition was not aggregated with accurate descendant totals.");
                Check(!events.Any(item => ((string)item["data"]["path"]).StartsWith("photos/")), "Directory activity duplicated every child.");
                Check(events.Any(item => (string)item["data"]["path"] == "one.txt" && (string)item["data"]["actor"] == "activity-source" && (string)item["data"]["action"] == "added"), "Single file activity lost the source device or path.");
                Check(events.Any(item => (string)item["data"]["path"] == "empty" && (long)item["data"]["files"] == 0), "Empty directory addition was not recorded.");
                var summary = LastSyncEvent(a);
                Check((long)summary["data"]["files"] == 3 && (long)summary["data"]["bytes"] == 14 && (long)summary["data"]["uploaded"] == 3 && (double)summary["data"]["durationMs"] >= 0 && ((string)summary["data"]["scope"]).Contains("activity-receiver"), "Sync metrics counted checked files or omitted scope, duration and bytes.");
                var received = LastSyncEvent(b);
                Check((long)received["data"]["files"] == 3 && (long)received["data"]["downloaded"] == 3 && (long)received["data"]["bytes"] == 14, "Receiver did not get the synchronization statistics.");
                var remoteEvents = FileEvents(b, "activity-details");
                Check(remoteEvents.Any(item => (string)item["data"]["actor"] == "activity-source" && (string)item["data"]["path"] == "photos" && (long)item["data"]["files"] == 2), "Receiver lost directory aggregation or the source device.");
                var incomingCount = remoteEvents.Length;
                b.Engine.ScanFolder(b.Config.GetFolder(id), true);
                Check(FileEvents(b, "activity-details").Length == incomingCount, "Received directories were incorrectly logged again as local additions.");
                Run(a, id); summary = LastSyncEvent(a);
                Check((long)summary["data"]["files"] == 0 && (long)summary["data"]["bytes"] == 0 && (long)summary["data"]["checked"] == 3, "Unchanged files were falsely counted as transferred.");
                baseline = FileEvents(a, "activity-details").Select(item => (string)item["id"]).ToArray();
                Put(a, id, "photos/a.txt", "changed one"); Run(a, id);
                events = FileEvents(a, "activity-details").Where(item => !baseline.Contains((string)item["id"])).ToArray();
                Check(events.Length == 1 && (string)events[0]["data"]["path"] == "photos/a.txt" && (string)events[0]["data"]["kind"] == "file" && (string)events[0]["data"]["action"] == "modified", "A single changed child was presented as a whole directory.");
                baseline = FileEvents(a, "activity-details").Select(item => (string)item["id"]).ToArray();
                Put(a, id, "photos/a.txt", "changed both a"); Put(a, id, "photos/nested/b.txt", "changed both b"); Run(a, id);
                events = FileEvents(a, "activity-details").Where(item => !baseline.Contains((string)item["id"])).ToArray();
                Check(events.Length == 1 && (string)events[0]["data"]["path"] == "photos" && (long)events[0]["data"]["files"] == 2, "A complete directory modification did not merge.");
                baseline = FileEvents(a, "activity-details").Select(item => (string)item["id"]).ToArray();
                RemoveRaceDirectory(FilePath(a, id, "photos"), root); Run(a, id);
                events = FileEvents(a, "activity-details").Where(item => !baseline.Contains((string)item["id"])).ToArray();
                Check(events.Length == 1 && (string)events[0]["data"]["path"] == "photos" && (string)events[0]["data"]["action"] == "deleted" && (long)events[0]["data"]["files"] == 2, "Directory deletion produced missing or duplicate activities.");
                Check((long)LastSyncEvent(a)["data"]["deleted"] == 2 && (long)LastSyncEvent(a)["data"]["files"] == 0, "Deleted files were counted as payload transfer.");
                var restored = new ActivityJournal(a.Config).Read(300);
                Check(restored.OfType<JObject>().Any(item => item["data"] != null && (string)item["data"]["path"] == "photos" && (long)item["data"]["files"] == 2), "Structured activity disappeared after restart.");
                var exported = a.Engine.Journal.Export(a.Engine.Status).ToString();
                Check(!exported.Contains("photos") && !exported.Contains("one.txt") && !exported.Contains("activity-source") && !exported.Contains("activity-receiver"), "Diagnostics exposed structured file activity details.");
                var api = Api(a, "/api/activity?limit=300", null, "GET");
                Check(api["activity"].Any(item => item["data"] != null && item["data"]["durationMs"] != null), "Activity API discarded statistics.");
                a.Engine.TestPeer(a.Config.GetPeer(b.Config.DeviceId));
                var deviceCount = a.Engine.Journal.Read(300).Count(item => (string)item["type"] == "device");
                a.Engine.TestPeer(a.Config.GetPeer(b.Config.DeviceId));
                Check(a.Engine.Journal.Read(300).Count(item => (string)item["type"] == "device") == deviceCount, "Steady online state spammed device activity.");
                b.Stop();
                try
                {
                    a.Engine.TestPeer(a.Config.GetPeer(b.Config.DeviceId));
                    Check(a.Engine.Journal.Read(300).Any(item => (string)item["type"] == "device" && item["data"] != null && (string)item["data"]["action"] == "offline" && ((string)item["detail"]).Contains("activity-details")), "Offline activity omitted the affected device and shared folders.");
                }
                finally { b.Start(); }
                Api(a, "/api/peers/" + b.Config.DeviceId, JsonUtil.Obj("paused", true), "PATCH");
                Check(a.Engine.Journal.Read(300).Any(item => (string)item["type"] == "device" && ((string)item["detail"]).Contains("暂停设备同步")), "Device setting change was not recorded.");
                Api(a, "/api/peers/" + b.Config.DeviceId, JsonUtil.Obj("paused", false), "PATCH");
                Console.WriteLine("Detailed activity regressions passed: " + (checks - beforeChecks) + " checks.");
            }
            finally { a.Config.Device["name"] = originalA; b.Config.Device["name"] = originalB; a.Config.GetPeer(b.Config.DeviceId)["name"] = peerA; b.Config.GetPeer(a.Config.DeviceId)["name"] = peerB; }
        }
    }
}
