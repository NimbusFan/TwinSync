using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal sealed class ResumableTransfers
    {
        internal const int ChunkSize = 4 * 1024 * 1024;
        private sealed class TransferBusyException : IOException { internal TransferBusyException() : base("接收端传输繁忙，稍后自动重试。") { } }
        private readonly ConfigStore config;
        private readonly SyncEngine engine;
        private readonly string directory;
        private readonly object[] gates = Enumerable.Range(0, 64).Select(i => new object()).ToArray();
        private readonly object createGate = new object();
        private readonly object maintenanceGate = new object();
        private DateTime lastCleanup;
        internal ResumableTransfers(ConfigStore config, SyncEngine engine) { this.config = config; this.engine = engine; directory = Path.Combine(config.Home, "transfers"); Directory.CreateDirectory(directory); Maintain(true); }
        internal static string Digest(string text) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        internal static string PrefixHash(string path, long length, Action check = null)
        {
            using (var input = File.OpenRead(path)) return PrefixHash(input, length, check);
        }
        internal static string PrefixHash(Stream input, long length, Action check = null)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536];
                while (length > 0) { if (check != null) check(); var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length)); if (n == 0) throw new EndOfStreamException(); sha.TransformBlock(buffer, 0, n, null, 0); length -= n; }
                sha.TransformFinalBlock(new byte[0], 0, 0); return Hex(sha.Hash);
            }
        }
        private string Meta(string id) { return SafeFiles.Join(directory, id + ".json"); }
        private static string Part(JObject folder, string id) { return TransferCache.RecoverUpload((string)folder["path"], id); }
        private object Gate(string id) { return gates[Convert.ToInt32(id.Substring(0, 2), 16) % gates.Length]; }
        internal HttpResponse Route(JObject peer, HttpRequest request)
        {
            try
            {
                if (request.Path == "/peer/transfers/start" && request.Method == "POST") return Start(peer, JObject.Parse(request.ReadBodyString(16384)));
                var match = Regex.Match(request.Path, "^/peer/transfers/([a-f0-9]{64})(/commit)?$");
                if (!match.Success) return HttpResponse.Text("Not found", 404);
                var id = match.Groups[1].Value;
                lock (Gate(id))
                {
                    var meta = JsonUtil.LoadObject(Meta(id));
                    if (meta == null || (string)meta["peerId"] != (string)peer["id"]) return HttpResponse.Text("Not found", 404);
                    var folder = config.GetFolder((string)meta["folderId"]); if (folder == null) throw new OperationCanceledException("文件夹已移除。");
                    var check = Validate(peer, folder, meta); check();
                    var part = Part(folder, id); var offset = (long)meta["offset"]; var incoming = (JObject)meta["entry"];
                    if (match.Groups[2].Success && request.Method == "POST")
                    {
                        try
                        {
                            if (offset != EntryUtil.SizeOf(incoming) || !File.Exists(part) || new FileInfo(part).Length != offset) throw new IOException("文件尚未传输完整。");
                            if (!DeviceSecurity.Equal(JsonUtil.Sha256File(part), EntryUtil.HashOf(incoming))) { RemoveCached(id, meta); throw new IOException("文件校验失败，请重新传输。"); }
                            AtomicFile.AssertUnchanged(SafeFiles.Join((string)folder["path"], EntryUtil.PathOf(incoming)), (string)meta["targetHash"]);
                            var result = engine.AcceptIncomingFile(peer, folder, EntryUtil.PathOf(incoming), incoming, part, check);
                            File.Delete(Meta(id)); return HttpResponse.Json(result, 200);
                        }
                        catch
                        {
                            try { meta["failed"] = true; JsonUtil.SaveAtomic(Meta(id), meta); RemoveCached(id, meta); } catch { }
                            throw;
                        }
                        finally { if (!File.Exists(part)) { try { File.Delete(Meta(id)); } catch { } } }
                    }
                    if (!match.Groups[2].Success && request.Method == "PUT")
                    {
                        long requested;
                        var hash = request.Query.ContainsKey("hash") ? request.Query["hash"] : "";
                        if (!request.Query.ContainsKey("offset") || !long.TryParse(request.Query["offset"], out requested) || requested != offset || request.ContentLength < 1 || request.ContentLength > ChunkSize || request.ContentLength > EntryUtil.SizeOf(incoming) - offset || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new IOException("分块位置、长度或校验信息无效。");
                        using (var output = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            if (output.Length < offset) throw new IOException("临时文件不完整，请重新开始传输。");
                            output.SetLength(offset); output.Position = offset;
                            try
                            {
                                using (var sha = SHA256.Create())
                                {
                                    var buffer = new byte[65536]; long remaining = request.ContentLength;
                                    while (remaining > 0) { check(); var n = request.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (n <= 0) throw new EndOfStreamException("传输中断，已保留之前完成的分块。"); check(); sha.TransformBlock(buffer, 0, n, null, 0); output.Write(buffer, 0, n); remaining -= n; }
                                    sha.TransformFinalBlock(new byte[0], 0, 0);
                                    if (!DeviceSecurity.Equal(Hex(sha.Hash), hash)) throw new IOException("分块校验失败。");
                                }
                                check(); output.Flush(true);
                                meta["offset"] = offset + request.ContentLength; meta["updatedAt"] = JsonUtil.NowIso(); JsonUtil.SaveAtomic(Meta(id), meta);
                            }
                            catch { output.SetLength(offset); output.Flush(true); throw; }
                        }
                        return HttpResponse.Json(JsonUtil.Obj("ok", true, "offset", meta["offset"]), 200);
                    }
                    return HttpResponse.Text("Method not allowed", 405);
                }
            }
            catch (OperationCanceledException error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 403); }
            catch (TransferBusyException error) { var response = HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 503); response.Headers["Retry-After"] = "2"; return response; }
            catch (Exception error) { return HttpResponse.Json(JsonUtil.Obj("ok", false, "error", error.Message), 409); }
        }
        private Action Validate(JObject peer, JObject folder, JObject meta)
        {
            lock (engine.ConfigurationGate)
            {
            if ((string)meta["binding"] != Digest(engine.TransferBinding(peer, folder))) throw new OperationCanceledException("同步授权已变更，请重新开始传输。");
            var incoming = (JObject)meta["entry"]; var local = engine.IndexFor(folder).Get(EntryUtil.PathOf(incoming));
            if (EntryUtil.HashOf(local) != (string)meta["localHash"] || EntryUtil.Deleted(local) != (bool)meta["localDeleted"]) throw new IOException("本地文件已变化，已保留本地修改。");
            return engine.TransferGuard(peer, folder, true, EntryUtil.PathOf(incoming));
            }
        }
        private HttpResponse Start(JObject peer, JObject body)
        {
            var folder = config.GetFolder((string)body["folder"]); if (folder == null) throw new OperationCanceledException("文件夹不存在。");
            engine.TransferGuard(peer, folder, true)();
            var supplied = body["entry"] as JObject; if (supplied == null) throw new IOException("缺少文件信息。");
            var path = EntryUtil.PathOf(supplied); var hash = EntryUtil.HashOf(supplied); var size = EntryUtil.SizeOf(supplied);
            if (string.IsNullOrEmpty(path) || path.Length > 4096 || size < 0 || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new IOException("文件信息无效。");
            var target = SafeFiles.UserPath((string)folder["path"], path);
            var binding = Digest(engine.TransferBinding(peer, folder));
            JObject before;
            lock (engine.ConfigurationGate) { var value = engine.IndexFor(folder).Get(path); before = value == null ? null : (JObject)value.DeepClone(); }
            var id = Digest(peer["id"] + "|" + folder["id"] + "|" + path + "|" + hash + "|" + size + "|" + binding + "|" + EntryUtil.HashOf(before) + "|" + EntryUtil.Deleted(before));
            lock (Gate(id))
            {
                JObject local;
                lock (engine.ConfigurationGate) { var value = engine.IndexFor(folder).Get(path); local = value == null ? null : (JObject)value.DeepClone(); }
                if (local != null && !EntryUtil.Deleted(local) && EntryUtil.HashOf(local) == hash && File.Exists(target) && DeviceSecurity.Equal(JsonUtil.Sha256File(target), hash))
                {
                    var old = JsonUtil.LoadObject(Meta(id)); if (old != null) RemoveCached(id, old);
                    engine.ApplyAcks(folder, new JArray(supplied.DeepClone()), peer);
                    return HttpResponse.Json(JsonUtil.Obj("ok", true, "complete", true, "entry", local.DeepClone()), 200);
                }
                var part = Part(folder, id); var meta = JsonUtil.LoadObject(Meta(id));
                if (meta != null && JsonUtil.GetBool(body["reset"], false)) { if (File.Exists(part)) File.Delete(part); File.Delete(Meta(id)); meta = null; }
                if (meta == null)
                {
                    lock (createGate)
                    {
                        var tasks = Directory.GetFiles(directory, "*.json");
                        if (tasks.Length >= 16) { Reclaim(false, true); tasks = Directory.GetFiles(directory, "*.json"); }
                        if (tasks.Length >= 128 || tasks.Count(p => { try { return (string)JsonUtil.LoadObject(p)["peerId"] == (string)peer["id"]; } catch { return false; } }) >= 16) throw new TransferBusyException();
                        var incoming = JsonUtil.Obj("path", path, "type", "file", "size", size, "hash", hash, "mtimeMs", EntryUtil.MtimeOf(supplied), "rev", EntryUtil.RevOf(supplied), "origin", EntryUtil.OriginOf(supplied), "deleted", false, "baseHash", "", "updatedAt", JsonUtil.NowIso());
                        meta = JsonUtil.Obj("peerId", peer["id"], "folderId", folder["id"], "root", folder["path"], "binding", binding, "entry", incoming, "offset", 0, "localHash", EntryUtil.HashOf(local), "localDeleted", EntryUtil.Deleted(local), "targetHash", File.Exists(target) ? JsonUtil.Sha256File(target) : null, "updatedAt", JsonUtil.NowIso());
                        using (var file = File.Create(part)) file.Flush(true);
                        JsonUtil.SaveAtomic(Meta(id), meta);
                    }
                }
                var check = Validate(peer, folder, meta); check();
                var offset = (long)meta["offset"];
                if (!File.Exists(part) || new FileInfo(part).Length < offset) { using (var file = File.Create(part)) file.Flush(true); offset = 0; meta["offset"] = 0; }
                using (var file = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(offset);
                meta["updatedAt"] = JsonUtil.NowIso(); JsonUtil.SaveAtomic(Meta(id), meta);
                return HttpResponse.Json(JsonUtil.Obj("ok", true, "id", id, "offset", offset, "prefixHash", PrefixHash(part, offset, check), "chunkSize", ChunkSize), 200);
            }
        }
        private void RemoveCached(string id, JObject meta)
        {
            // Only our reserved cache filename is removable. Never delete the destination entry path.
            var root = JsonUtil.GetString(meta["root"], "");
            if (!string.IsNullOrEmpty(root))
            {
                var part = TransferCache.ExistingUpload(root, id); if (File.Exists(part)) File.Delete(part);
                var legacy = SafeFiles.Join(root, ".twinsync-part-" + id); if (File.Exists(legacy)) File.Delete(legacy);
            }
            File.Delete(Meta(id));
        }
        private void Reclaim(bool startup, bool pressure)
        {
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path); if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) continue;
                var taskGate = Gate(id);
                if (!System.Threading.Monitor.TryEnter(taskGate)) continue;
                try
                {
                    var meta = JsonUtil.LoadObject(path); if (meta == null) continue;
                    DateTime at; var expired = !DateTime.TryParse((string)meta["updatedAt"], out at) || DateTime.UtcNow - at.ToUniversalTime() >= TimeSpan.FromDays(7);
                    var offset = JsonUtil.GetLong(meta["offset"], -1); var entry = meta["entry"] as JObject;
                    bool obsolete;
                    lock (engine.ConfigurationGate)
                    {
                        var folder = config.GetFolder((string)meta["folderId"]); var peer = config.GetPeer((string)meta["peerId"]);
                        var current = folder == null || entry == null ? null : engine.IndexFor(folder).Get(EntryUtil.PathOf(entry));
                        obsolete = JsonUtil.GetBool(meta["failed"], false) || folder == null || peer == null || entry == null || offset < 0 || offset > EntryUtil.SizeOf(entry)
                            || (string)meta["binding"] != Digest(engine.TransferBinding(peer, folder))
                            || EntryUtil.HashOf(current) != (string)meta["localHash"] || EntryUtil.Deleted(current) != JsonUtil.GetBool(meta["localDeleted"], false);
                    }
                    var root = JsonUtil.GetString(meta["root"], "");
                    if (startup && !obsolete && !expired && offset > 0 && !string.IsNullOrEmpty(root)) TransferCache.RecoverUpload(root, id);
                    var part = string.IsNullOrEmpty(root) ? null : TransferCache.ExistingUpload(root, id);
                    var missing = part == null || !File.Exists(part);
                    // Empty attempts are not resumable data; reclaim them when starting or admitting another transfer.
                    if (expired || obsolete || missing || offset == 0 && (startup || DateTime.UtcNow - at.ToUniversalTime() > TimeSpan.FromMinutes(2) || pressure && DateTime.UtcNow - at.ToUniversalTime() > TimeSpan.FromSeconds(2))) RemoveCached(id, meta);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (Newtonsoft.Json.JsonException) { try { File.Delete(path); } catch { } }
                catch (Exception) { } // A damaged cache entry must not prevent service startup.
                finally { System.Threading.Monitor.Exit(taskGate); }
            }
        }
        internal void Maintain(bool startup = false, bool force = false)
        {
            if (!System.Threading.Monitor.TryEnter(maintenanceGate)) return;
            try
            {
                if (!startup && !force && DateTime.UtcNow - lastCleanup < TimeSpan.FromSeconds(30)) return;
                lastCleanup = DateTime.UtcNow; Reclaim(startup, false);
                lock (engine.ConfigurationGate)
                {
                    var ids = Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).ToArray();
                    foreach (var folder in config.Folders.OfType<JObject>())
                    {
                        try
                        {
                            var root = (string)folder["path"];
                            if (startup) TransferCache.MigrateOrphans(root, ids);
                            TransferCache.Maintain(root, engine.IndexFor(folder));
                        }
                        catch (Exception error) { engine.AddActivity("error", "传输缓存维护未完成", error.Message, "warn"); }
                    }
                }
            }
            finally { System.Threading.Monitor.Exit(maintenanceGate); }
        }
    }
}
