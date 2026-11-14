using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class NativeSecurityTests
    {
        private static int checks;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
        private static string Call(int port, string path, string method, string body, string csrf, out int status, string origin, string host)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            request.Proxy = null; request.Method = method; request.KeepAlive = false; request.Timeout = 12000; request.ServicePoint.Expect100Continue = false;
            if (csrf != null) request.Headers["x-twinsync-csrf"] = csrf;
            if (origin != null) request.Headers["Origin"] = origin;
            if (host != null) request.Host = host;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body); request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException error) { response = (HttpWebResponse)error.Response; if (response == null) throw new Exception(method + " " + path + ": " + error.Message, error); }
            using (response) using (var input = new StreamReader(response.GetResponseStream()))
            { status = (int)response.StatusCode; try { return input.ReadToEnd(); } catch (Exception error) { throw new Exception(method + " " + path + " (" + status + "): " + error.Message, error); } }
        }
        private static JObject Api(CoreService core, string csrf, string route, JObject body)
        {
            int status; var text = Call(core.Port, route, body == null ? "GET" : "POST", body == null ? null : body.ToString(), csrf, out status, null, null);
            if (status < 200 || status > 299) throw new Exception("API " + route + ": " + status + " " + text);
            return JObject.Parse(text);
        }
        private static string Csrf(CoreService core)
        {
            int status; var html = Call(core.Port, "/", "GET", null, null, out status, null, null);
            return Regex.Match(html, "name=\"twinsync-csrf\" content=\"([^\"]+)\"").Groups[1].Value;
        }
        private static void CheckFileList(CoreService core, string csrf, string root)
        {
            var folder = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "列表回归", "path", Path.Combine(root, "list-only"), "mode", "two-way", "enabled", true, "paused", true, "peerIds", new JArray());
            Directory.CreateDirectory(JsonUtil.GetString(folder["path"], ""));
            core.Config.Folders.Add(folder);
            var index = core.Engine.IndexFor(folder);
            for (var number = 0; number < 125; number++) index.Upsert(JsonUtil.Obj("path", "docs/file-" + number.ToString("D3") + ".txt", "size", number, "hash", "current-hash", "baseHash", number % 5 == 0 ? "old-hash" : "current-hash", "deleted", false));
            index.Upsert(JsonUtil.Obj("path", "docs/deleted.txt", "deleted", true));
            index.AddConflict(JsonUtil.Obj("id", JsonUtil.RandomId(), "path", "docs/file-001.txt"));
            var route = "/api/folders/" + JsonUtil.GetString(folder["id"], "") + "/files";
            var result = Api(core, csrf, route, null);
            Check(((JArray)result["files"]).Count == 50 && (int)result["total"] == 125 && (int)result["limit"] == 50, "File list defaults to 50 active files per page");
            result = Api(core, csrf, route + "?offset=50&limit=50", null);
            Check(((JArray)result["files"]).Count == 50 && result["files"][0]["path"].ToString() == "docs/file-050.txt", "Second page must have stable ordering without duplicates");
            result = Api(core, csrf, route + "?offset=100&limit=50", null);
            Check(((JArray)result["files"]).Count == 25 && (int)result["offset"] == 100, "Last page must retain its actual offset and total");
            result = Api(core, csrf, route + "?offset=999&limit=50", null);
            Check((int)result["offset"] == 100 && ((JArray)result["files"]).Count == 25, "Outdated offsets clamp to the last populated page");
            result = Api(core, csrf, route + "?query=FILE-12", null);
            Check((int)result["total"] == 5 && ((JArray)result["files"]).Count == 5, "Search must filter before pagination and ignore case");
            result = Api(core, csrf, route + "?status=pending", null);
            Check((int)result["total"] == 25 && result["files"][0]["status"].ToString() == "pending", "Pending filter must match unsynchronized hashes");
            result = Api(core, csrf, route + "?status=synced", null);
            Check((int)result["total"] == 99 && result["files"][0]["status"].ToString() == "synced", "Synced filter must exclude pending and conflicted files");
            result = Api(core, csrf, route + "?status=conflict", null);
            Check((int)result["total"] == 1 && (bool)result["files"][0]["conflicted"], "Conflict filter must retain its specific file");
            result = Api(core, csrf, route + "?query=missing&offset=999", null);
            Check((int)result["total"] == 0 && (int)result["offset"] == 0 && ((JArray)result["files"]).Count == 0, "An empty search returns a valid empty first page");
            result = Api(core, csrf, route + "?limit=999", null);
            Check((int)result["limit"] == 200, "Page size must be bounded");
            result = Api(core, csrf, route + "?limit=invalid&offset=-1", null);
            Check((int)result["limit"] == 50 && (int)result["offset"] == 0, "Invalid paging parameters use safe defaults");
            Check(EntryUtil.Deleted(index.Get("docs/deleted.txt")), "Listing must preserve deletion records for synchronization");
            var browse = "/api/folders/" + JsonUtil.GetString(folder["id"], "") + "/browse";
            result = Api(core, csrf, browse, null);
            Check((int)result["total"] == 1 && result["children"][0]["type"].ToString() == "directory" && result["children"][0]["name"].ToString() == "docs", "Browse groups descendants into one directory at the root");
            Check((int)result["fileCount"] == 125 && (long)result["totalBytes"] == 7750 && (long)result["children"][0]["size"] == 7750, "Directory aggregates include every active descendant across pages");
            Check((int)result["children"][0]["pendingCount"] == 25 && (int)result["children"][0]["conflictCount"] == 1, "Folder tiles retain descendant sync status counts");
            result = Api(core, csrf, browse + "?directory=docs&offset=50", null);
            Check((int)result["total"] == 125 && ((JArray)result["children"]).Count == 50 && result["children"][0]["path"].ToString() == "docs/file-050.txt", "Directory navigation paginates only immediate children");
            long mapBytes = 0; int mapFiles = 0;
            foreach (JObject item in (JArray)result["mapItems"]) { mapBytes += (long)item["size"]; mapFiles += (int)item["fileCount"]; }
            Check(((JArray)result["mapItems"]).Count == 24 && mapBytes == 7750 && mapFiles == 125, "Bounded space-map data preserves all totals including grouped small items");
            Check((int)result["zeroItems"] == 1, "Zero-byte files remain discoverable outside the area chart");
            result = Api(core, csrf, browse + "?status=pending", null);
            Check((int)result["fileCount"] == 25 && (long)result["totalBytes"] == 1500, "Directory aggregation honors pending filters before grouping");
            result = Api(core, csrf, browse + "?query=FILE-12", null);
            Check((int)result["fileCount"] == 5 && (int)result["total"] == 1, "Recursive search keeps matching results grouped in their folder");
            index.Upsert(JsonUtil.Obj("path", "docs-other/extra.txt", "size", 100, "hash", "same", "baseHash", "same"));
            result = Api(core, csrf, browse + "?directory=docs", null);
            Check((int)result["fileCount"] == 125, "Directory prefix cannot include a similarly named sibling");
            index.Upsert(JsonUtil.Obj("path", "zeros/empty.txt", "size", 0, "hash", "same", "baseHash", "same"));
            result = Api(core, csrf, browse + "?directory=zeros", null);
            Check((long)result["totalBytes"] == 0 && (int)result["fileCount"] == 1 && ((JArray)result["mapItems"]).Count == 1, "All-zero directories retain file counts for the chart fallback");
            int denied;
            Call(core.Port, browse + "?directory=..%2Foutside", "GET", null, csrf, out denied, null, null);
            Check(denied == 400, "Folder navigation must reject parent traversal");
            Call(core.Port, browse + "?directory=C%3A%2FWindows", "GET", null, csrf, out denied, null, null);
            Check(denied == 400, "Folder navigation must reject absolute filesystem paths");
            var freshPath = Path.Combine(root, "fresh-browse");
            Directory.CreateDirectory(freshPath); File.WriteAllText(Path.Combine(freshPath, "existing.txt"), "existing data");
            var added = Api(core, csrf, "/api/folders", JsonUtil.Obj("name", "首次浏览", "path", freshPath, "mode", "two-way", "peerIds", new JArray()));
            result = Api(core, csrf, "/api/folders/" + added["folder"]["id"].ToString() + "/browse", null);
            Check((int)result["fileCount"] == 1 && result["children"][0]["name"].ToString() == "existing.txt", "First browsing of a new selection discovers existing files without requiring a peer");
        }
        private static void CheckReveal(CoreService core, string csrf, string root, List<string> calls)
        {
            var directory = Path.Combine(root, "reveal 目录");
            var nested = Path.Combine(directory, "子目录");
            Directory.CreateDirectory(nested);
            var file = Path.Combine(nested, "报告, #1.txt");
            File.WriteAllText(file, "test fixture");
            var added = Api(core, csrf, "/api/folders", JsonUtil.Obj("name", "定位文件", "path", directory, "mode", "two-way", "peerIds", new JArray()));
            var route = "/api/folders/" + added["folder"]["id"] + "/reveal";
            Api(core, csrf, route, JsonUtil.Obj("path", "子目录/报告, #1.txt"));
            Check(calls.Count == 1 && calls[0] == file, "Reveal must read the selected file from the POST body, not silently open the root");
            Api(core, csrf, route + "?path=missing.txt", JsonUtil.Obj("path", "子目录/报告, #1.txt"));
            Check(calls.Count == 2 && calls[1] == file, "Reveal body path must take precedence over legacy query parameters");
            Api(core, csrf, route, new JObject());
            Check(calls.Count == 3 && calls[2].TrimEnd('\\') == directory, "Empty reveal path must open the selected sync directory");
            Api(core, csrf, route, JsonUtil.Obj("path", "子目录"));
            Check(calls.Count == 4 && calls[3] == nested, "Revealing a directory must open that directory, not its parent");
            Api(core, csrf, route + "?path=" + Uri.EscapeDataString("子目录/报告, #1.txt"), new JObject());
            Check(calls.Count == 5 && calls[4] == file, "Existing reveal query links must remain supported");
            int status;
            foreach (var invalid in new[] { "../outside.txt", file, "子目录/../../outside", "missing.txt", "file.txt:stream" })
            {
                Call(core.Port, route, "POST", JsonUtil.Obj("path", invalid).ToString(), csrf, out status, null, null);
                Check(status == 400 && calls.Count == 5, "Invalid reveal path must fail before reaching Windows Shell: " + invalid);
            }
            Call(core.Port, route, "POST", null, null, out status, null, null);
            Check(status == 403 && calls.Count == 5, "Reveal must preserve CSRF validation");
            Api(core, csrf, "/api/reveal", JsonUtil.Obj("path", file));
            Check(calls.Count == 6 && calls[5] == file, "Global reveal must retain the exact file path");
            Api(core, csrf, "/api/reveal", JsonUtil.Obj("path", nested));
            Check(calls.Count == 7 && calls[6] == nested, "Global reveal must open a directory itself");
            foreach (var invalid in new[] { "", "relative.txt", "C:relative.txt", Path.Combine(root, "missing-file.txt") })
            {
                Call(core.Port, "/api/reveal", "POST", JsonUtil.Obj("path", invalid).ToString(), csrf, out status, null, null);
                Check(status == 400 && calls.Count == 7, "Global reveal must reject missing/relative paths");
            }
        }

        public static int Main(string[] args)
        {
            CoreService a = null, b = null;
            try
            {
                var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                var revealCalls = new List<string>();
                a = new CoreService(args[1], Path.Combine(root, "home-a"), FreePort(), path => { lock (revealCalls) revealCalls.Add(path); });
                b = new CoreService(args[1], Path.Combine(root, "home-b"), FreePort());
                a.Config.Device["port"] = a.Port; b.Config.Device["port"] = b.Port;
                a.Config.Settings["discoveryEnabled"] = false; b.Config.Settings["discoveryEnabled"] = false;
                Check(a.Config.Folders.Count == 0 && b.Config.Folders.Count == 0, "Fresh installs must have no sync folders");
                Check(JsonUtil.GetString(a.Config.Device["name"], "") == "", "Device name must be chosen by the user");
                a.Start(); b.Start();
                var csrfA = Csrf(a); var csrfB = Csrf(b);
                Check(csrfA.Length >= 40 && csrfB.Length >= 40 && csrfA != csrfB, "Panel CSRF tokens must be unique");
                int status;
                Call(a.Port, "/api/pairing/refresh", "POST", null, null, out status, null, null); Check(status == 403, "Mutation without CSRF must fail");
                Call(a.Port, "/api/state", "GET", null, null, out status, "https://attacker.invalid", null); Check(status == 403, "Cross-origin reads must fail");
                Call(a.Port, "/api/state", "GET", null, null, out status, null, "attacker.invalid:" + a.Port); Check(status == 403, "DNS rebinding Host must fail");
                Call(a.Port, "/peer/ping", "GET", null, null, out status, null, null); Check(status == 403, "Plaintext peer traffic must fail");
                Api(a, csrfA, "/api/device", JsonUtil.Obj("name", "用户自定义电脑 A"));
                Check(a.Config.DeviceName == "用户自定义电脑 A", "Custom names must persist");
                var state = Api(a, csrfA, "/api/state", null);
                Check(state["device"]["secret"] == null, "State must not expose device secret");
                var code = JsonUtil.GetString(state["pairing"]["code"], "");
                var badPin = new string('0', 64);
                var failed = false;
                try { b.Engine.PairOutgoing("127.0.0.1", a.Port, code, badPin); } catch { failed = true; }
                Check(failed && a.Config.Peers.Count == 0 && b.Config.Peers.Count == 0, "Wrong certificate pin must fail before pairing");
                Api(b, csrfB, "/api/peers/pair", JsonUtil.Obj("host", "127.0.0.1", "port", a.Port, "code", code, "fingerprint", a.Security.Fingerprint));
                Check(a.Config.Peers.Count == 1 && b.Config.Peers.Count == 1, "TLS pairing must work in both directions");
                Check(a.Config.Folders.Count == 0 && b.Config.Folders.Count == 0, "Pairing must not create or share folders");
                var peerA = b.Config.GetPeer(a.Config.DeviceId); var peerB = a.Config.GetPeer(b.Config.DeviceId);
                Check(new PeerClient(peerA, b.Config.DeviceId).Ping()["device"]["secret"] == null, "Peer ping must not expose secrets");
                Check(Api(a, csrfA, "/api/state", null)["peers"][0]["token"] == null, "State must not expose peer tokens");
                var persisted = File.ReadAllText(a.Config.ConfigPath);
                Check(!persisted.Contains(JsonUtil.GetString(peerB["token"], "")) && persisted.Contains("dpapi:"), "Peer tokens must be encrypted at rest");
                Check(new ConfigStore(a.Config.Home).Peers.Count == 1, "Protected configuration must reload");
                Check(new DeviceSecurity(a.Config.Home).Fingerprint == a.Security.Fingerprint, "Device identity must survive restart");
                var folderA = Path.Combine(root, "selected-a"); var folderB = Path.Combine(root, "selected-b");
                Directory.CreateDirectory(folderA); File.WriteAllText(Path.Combine(folderA, "测试.txt"), "first version", new UTF8Encoding(false));
                var addedA = Api(a, csrfA, "/api/folders", JsonUtil.Obj("name", "用户选择目录", "path", folderA, "mode", "two-way", "peerIds", new JArray(b.Config.DeviceId)));
                var folderId = JsonUtil.GetString(addedA["folder"]["id"], "");
                for (var attempt = 0; attempt < 50 && b.Engine.PendingFolders().Count == 0; attempt++) Thread.Sleep(100);
                Check(b.Engine.PendingFolders().Count == 1 && b.Config.Folders.Count == 0 && !Directory.Exists(folderB), "Remote share must wait for a local directory choice");
                Check(!b.Engine.PendingFolders().ToString().Contains(folderA), "Remote offers must not disclose absolute paths");
                Api(b, csrfB, "/api/folders", JsonUtil.Obj("offerId", folderId, "name", "本机选择目录", "path", folderB, "mode", "two-way", "peerIds", new JArray(a.Config.DeviceId)));
                a.Engine.SyncAll(true);
                Check(File.Exists(Path.Combine(folderB, "测试.txt")) && File.ReadAllText(Path.Combine(folderB, "测试.txt")) == "first version", "Encrypted sync must transfer into the separately chosen path");
                File.WriteAllText(Path.Combine(folderB, "测试.txt"), "second version", new UTF8Encoding(false));
                b.Engine.SyncAll(true);
                Check(File.ReadAllText(Path.Combine(folderA, "测试.txt")) == "second version", "Two-way sync must remain functional");
                Check(a.Versions.Count > 0, "Overwrites must preserve history");
                foreach (var relative in new[] { "../escape.txt", "../selected-ab/escape.txt", "C:/escape.txt", "file.txt:stream", "CON", "nested/../escape.txt" })
                {
                    failed = false; try { SafeFiles.Join(folderA, relative); } catch { failed = true; } Check(failed, "Unsafe path must fail: " + relative);
                }
                var unshared = JsonUtil.Obj("id", JsonUtil.RandomId(), "name", "私有目录", "path", Path.Combine(root, "private"), "peerIds", new JArray());
                a.Config.Folders.Add(unshared); a.Config.Save();
                failed = false;
                try { new PeerClient(peerA, b.Config.DeviceId).Delete(JsonUtil.GetString(unshared["id"], ""), "keep.txt", JsonUtil.Obj("rev", 1, "origin", b.Config.DeviceId)); } catch { failed = true; }
                Check(failed, "Unshared folders must reject deletion");
                failed = false;
                try { new PeerClient(peerA, b.Config.DeviceId).Ack(JsonUtil.GetString(unshared["id"], ""), new JArray()); } catch { failed = true; }
                Check(failed, "Unshared folders must reject acknowledgement changes");
                a.Config.GetFolder(folderId)["mode"] = "send-only";
                failed = false;
                try { new PeerClient(peerA, b.Config.DeviceId).Delete(folderId, "测试.txt", JsonUtil.Obj("rev", 999, "origin", b.Config.DeviceId)); } catch { failed = true; }
                Check(failed && File.Exists(Path.Combine(folderA, "测试.txt")), "Send-only folders must reject remote deletion");
                a.Config.GetFolder(folderId)["paused"] = true;
                failed = false; try { new PeerClient(peerA, b.Config.DeviceId).Manifest(folderId); } catch { failed = true; }
                Check(failed, "Paused folders must reject peer access");
                a.Config.Peers.Remove(peerB);
                failed = false; try { new PeerClient(peerA, b.Config.DeviceId).Ping(); } catch { failed = true; }
                Check(failed, "Removing a device must revoke its token");
                var validCode = JsonUtil.GetString(Api(a, csrfA, "/api/state", null)["pairing"]["code"], "");
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    try { b.Engine.PairOutgoing("127.0.0.1", a.Port, validCode == "000000" ? "111111" : "000000", a.Security.Fingerprint); } catch { }
                }
                failed = false; try { b.Engine.PairOutgoing("127.0.0.1", a.Port, validCode, a.Security.Fingerprint); } catch { failed = true; }
                Check(failed, "Pairing retries must be limited even with the correct code after lockout");
                Api(a, csrfA, "/api/pairing/refresh", new JObject());
                validCode = JsonUtil.GetString(Api(a, csrfA, "/api/state", null)["pairing"]["code"], "");
                b.Engine.PairOutgoing("127.0.0.1", a.Port, validCode, a.Security.Fingerprint);
                Check(a.Config.Peers.Count == 1, "Local pairing refresh must recover from lockout");
                CheckFileList(a, csrfA, root);
                CheckReveal(a, csrfA, root, revealCalls);
                Console.WriteLine("Native security and encrypted two-device regression passed: " + checks + " checks.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
            finally { if (a != null) a.Stop(); if (b != null) b.Stop(); }
        }
    }
}
