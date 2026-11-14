using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static partial class WorkflowTests
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static int Port() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
        private static JObject Api(CoreService core, string route, JObject body, string method = "POST", int expected = 200)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + core.Port + route);
            request.Proxy = null; request.Method = method; request.KeepAlive = false; request.Timeout = 15000; request.ServicePoint.Expect100Continue = false;
            request.Headers["x-twinsync-csrf"] = core.Security.CsrfToken;
            if (body != null) { var bytes = Encoding.UTF8.GetBytes(body.ToString()); request.ContentType = "application/json"; request.ContentLength = bytes.Length; using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length); }
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException error) { response = error.Response as HttpWebResponse; if (response == null) throw; }
            using (response) using (var input = new StreamReader(response.GetResponseStream()))
            {
                var text = input.ReadToEnd();
                if ((int)response.StatusCode != expected) throw new Exception(route + ": " + response.StatusCode + " " + text);
                return JObject.Parse(text);
            }
        }
        private static JObject Run(CoreService core, string folderId = null, string peerId = null)
        {
            var queued = Api(core, "/api/sync", JsonUtil.Obj("folderId", folderId, "peerId", peerId), "POST", 202);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 15000)
            {
                var state = Api(core, "/api/state", null, "GET");
                if (state["sync"]["runId"].ToString() == queued["runId"].ToString() && state["sync"]["finishedAt"].Type != JTokenType.Null) return (JObject)state["sync"];
                Thread.Sleep(20);
            }
            throw new Exception("Sync did not finish.");
        }
        private static string Share(CoreService source, CoreService target, string root, string name)
        {
            var path = Path.Combine(root, name + "-a"); Directory.CreateDirectory(path);
            var added = Api(source, "/api/folders", JsonUtil.Obj("name", name, "path", path, "mode", "two-way", "peerIds", new JArray(target.Config.DeviceId)), "POST", 201);
            var id = added["folder"]["id"].ToString();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!target.Engine.PendingFolders().OfType<JObject>().Any(item => item["id"].ToString() == id) && timer.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Api(target, "/api/folders", JsonUtil.Obj("name", name, "offerId", id, "path", Path.Combine(root, name + "-b"), "mode", "two-way", "peerIds", new JArray(source.Config.DeviceId)), "POST", 201);
            return id;
        }
        private static string FilePath(CoreService core, string id, string name) { return Path.Combine(core.Config.GetFolder(id)["path"].ToString(), name); }
        private static void Put(CoreService core, string id, string name, string value) { var file = FilePath(core, id, name); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, value); }
        private static string Last(CoreService core, string id) { return core.Engine.IndexFor(core.Config.GetFolder(id)).LastSyncAt; }
        private static void Pair(CoreService a, CoreService b) { try { Api(a, "/api/peers/pair", JsonUtil.Obj("host", "127.0.0.1", "port", b.Port, "code", ((JObject)Api(b, "/api/state", null, "GET")["pairing"])["code"], "fingerprint", b.Security.Fingerprint)); } catch (Exception error) { throw new Exception("Pair " + a.Port + " to " + b.Port, error); } }

        private static void Verify(CoreService a, CoreService b, CoreService c, string root)
        {
            Pair(a, b); Pair(a, c);
            var one = Share(a, b, root, "one"); var two = Share(a, b, root, "two"); var three = Share(a, c, root, "three");
            Put(a, one, "file.txt", "one"); Put(a, two, "file.txt", "two"); Put(a, three, "file.txt", "three");
            var result = Run(a, one);
            Check(result["lastResult"].ToString() == "success" && File.Exists(FilePath(b, one, "file.txt")), "Scoped folder sync did not transfer its file");
            Check(!File.Exists(FilePath(b, two, "file.txt")) && !File.Exists(FilePath(c, three, "file.txt")), "Folder sync touched another folder/device");
            Check(Last(a, two) == null && Last(a, three) == null, "Folder sync changed unrelated success timestamps");
            result = Run(a, null, b.Config.DeviceId);
            Check(result["lastResult"].ToString() == "success" && File.Exists(FilePath(b, two, "file.txt")), "Scoped device sync did not include its shared folder");
            Check(!File.Exists(FilePath(c, three, "file.txt")) && Last(a, three) == null, "Device sync touched an unselected device");
            var previousSuccess = a.Engine.Status["lastSyncAt"].ToString();
            result = Api(a, "/api/sync", JsonUtil.Obj("folderId", three, "peerId", b.Config.DeviceId));
            Check(!(bool)result["accepted"] && a.Engine.Status["lastResult"].ToString() == "skipped", "Unshared scope must be skipped");
            Check(a.Engine.Status["lastSyncAt"].ToString() == previousSuccess, "Skipped run updated success time");
            Api(a, "/api/sync", JsonUtil.Obj("folderId", Guid.NewGuid().ToString()), "POST", 400);
            Api(a, "/api/sync", JsonUtil.Obj("folderId", ""), "POST", 400);
            result = Run(a);
            Check(result["lastResult"].ToString() == "success" && File.Exists(FilePath(c, three, "file.txt")), "All-folder sync stopped working");
            var oneTime = Last(a, one); var threeTime = Last(a, three); previousSuccess = result["lastSyncAt"].ToString();
            b.Stop(); Put(a, three, "new.txt", "still transferred");
            result = Run(a);
            Check(result["lastResult"].ToString() == "partial" && ((JArray)result["errors"]).Count > 0, "Offline peer was incorrectly reported as complete");
            Check(Last(a, one) == oneTime && Last(a, three) != threeTime && File.Exists(FilePath(c, three, "new.txt")), "Failed and successful folder timestamps were not separated");
            Check(result["lastSyncAt"].ToString() == previousSuccess, "Partial run refreshed global success time");
            c.Stop(); result = Run(a);
            Check(result["lastResult"].ToString() == "failed" && result["lastSyncAt"].ToString() == previousSuccess, "All failures were reported as success");
            b.Start(); c.Start();
            Put(a, one, "good.txt", "deliver me"); Put(a, one, "blocked.txt", "keep me"); Directory.CreateDirectory(FilePath(b, one, "blocked.txt"));
            result = Run(a, one);
            Check(result["lastResult"].ToString() == "partial" && File.Exists(FilePath(b, one, "good.txt")), "Single-file failure stopped other files or reported total success");
            Check(((JArray)result["errors"]).ToString().Contains("blocked.txt") && Last(a, one) == oneTime, "Failed file detail or retained timestamp missing");

            var migration = Share(a, b, root, "migration"); Put(a, migration, "keep.txt", "preserve both locations"); Run(a, migration);
            var oldPath = a.Config.GetFolder(migration)["path"].ToString(); var newPath = Path.Combine(root, "new 中文目录");
            Api(a, "/api/folders/" + migration, JsonUtil.Obj("path", newPath), "PATCH", 400);
            Check(!Directory.Exists(newPath) && a.Config.GetFolder(migration)["path"].ToString() == oldPath, "Unconfirmed directory change mutated files/config");
            Api(a, "/api/folders/" + migration, JsonUtil.Obj("path", newPath, "pathChangeMode", "migrate", "paused", false), "PATCH");
            Check((bool)a.Config.GetFolder(migration)["paused"] && File.ReadAllText(Path.Combine(oldPath, "keep.txt")) == "preserve both locations" && File.ReadAllText(Path.Combine(newPath, "keep.txt")) == "preserve both locations", "Migration failed to retain originals or pause");
            Check(Last(a, migration) == null, "Migrated directory retained a stale success timestamp");
            Api(a, "/api/folders/" + migration, JsonUtil.Obj("paused", false), "PATCH"); Run(a, migration);
            Check(File.ReadAllText(FilePath(b, migration, "keep.txt")) == "preserve both locations" && !EntryUtil.Deleted(a.Engine.IndexFor(a.Config.GetFolder(migration)).Get("keep.txt")), "Migration generated a deletion on the other computer");
            var collision = Path.Combine(root, "collision"); Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "keep.txt"), "different local data");
            Api(a, "/api/folders/" + migration, JsonUtil.Obj("path", collision, "pathChangeMode", "migrate"), "PATCH", 400);
            Check(a.Config.GetFolder(migration)["path"].ToString() == newPath && File.ReadAllText(Path.Combine(collision, "keep.txt")) == "different local data", "Conflicting target was overwritten or bound");
            Api(a, "/api/folders/" + migration, JsonUtil.Obj("path", Path.Combine(newPath, "nested"), "pathChangeMode", "migrate"), "PATCH", 400);
            Check(!Directory.Exists(Path.Combine(newPath, "nested")), "Nested migration target was created");
            lock (a.Engine.ConfigurationGate)
            {
                a.Engine.QueueSync(true, migration, null);
                var changed = false; var rejected = false;
                try { a.Engine.ChangeConfiguration(delegate { changed = true; }); } catch { rejected = true; }
                Check(rejected && !changed, "Directory configuration was allowed during an active run");
            }
            var wait = System.Diagnostics.Stopwatch.StartNew(); while (a.Engine.Status["finishedAt"].Type == JTokenType.Null && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        }

        private static void StartupApi(CoreService core, List<bool> registrations, Action<bool> fail)
        {
            Api(core, "/api/settings", JsonUtil.Obj("openAtLogin", true), "PATCH");
            Check((bool)core.Config.Settings["openAtLogin"] && registrations[registrations.Count - 1], "Startup enable was only persisted, not applied");
            Api(core, "/api/settings", JsonUtil.Obj("openAtLogin", false), "PATCH");
            Check(!(bool)core.Config.Settings["openAtLogin"] && !registrations[registrations.Count - 1], "Startup disable was not applied");
            fail(true); Api(core, "/api/settings", JsonUtil.Obj("openAtLogin", true), "PATCH", 400); fail(false);
            Check(!(bool)core.Config.Settings["openAtLogin"], "Failed startup registration persisted an enabled setting");
        }
        public static int Main(string[] args)
        {
            CoreService a = null, b = null, c = null;
            try
            {
                var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var registrations = new List<bool>(); var failStartup = false;
                HttpServer.DiagnosticError = error => Console.Error.WriteLine("HTTP diagnostic: " + error);
                a = new CoreService(args[1], Path.Combine(root, "home-a"), Port(), null, enabled => { if (failStartup) throw new Exception("Test startup failure"); registrations.Add(enabled); });
                b = new CoreService(args[1], Path.Combine(root, "home-b"), Port(), null, enabled => { });
                c = new CoreService(args[1], Path.Combine(root, "home-c"), Port(), null, enabled => { });
                foreach (var core in new[] { a, b, c }) { core.Config.Settings["autoSync"] = false; core.Config.Device["port"] = core.Port; core.Start(); }
                Verify(a, b, c, root); StartupApi(a, registrations, value => failStartup = value); Reliability(a, b, root);
                Console.WriteLine("Workflow regressions passed: " + checks + " checks (scoped sync, failures, migration, startup, atomic files, conflicts, authorization, versions, byte progress)."); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { foreach (var core in new[] { a, b, c }) if (core != null) core.Stop(); }
        }
    }
}
