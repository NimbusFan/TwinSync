using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native {
    internal static class IndexTests {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static JObject Entry(string path, long size, bool deleted) {
            return JsonUtil.Obj("path",path,"type","file","size",size,"mtimeMs",1234567890.25,"hash",deleted?null:new string('a',64),"rev",7,"origin","test-device","deleted",deleted,"baseHash","","updatedAt","2026-11-13T00:00:00Z");
        }
        private static void Seed(string directory, string id, int count) {
            var entries = new JArray();
            for (int i=0;i<count;i++) entries.Add(Entry("folder/file-"+i+".txt",1024,false));
            JsonUtil.SaveAtomic(Path.Combine(directory,"index-"+id+".json"),JsonUtil.Obj("version",1,"folderId",id,"lamport",12,"lastScanAt",null,"lastSyncAt",null,"savedAt",null,"customMetadata","preserve-me","entries",entries,"conflicts",new JArray()));
        }
        private static void RoundTrip(string directory) {
            Seed(directory,"roundtrip",0);
            var index = new IndexStore(directory,"roundtrip");
            index.Upsert(Entry("项目/笔记.md",111,false));
            index.Upsert(Entry("old.txt",0,true));
            index.Upsert(Entry("new.txt",7,false));
            index.AddConflict(JsonUtil.Obj("id","conflict-1","path","项目/笔记.md","message","两份内容都保留"));
            index.LastScanAt="2026-11-13T01:00:00Z";
            index.LastSyncAt="2026-11-13T01:01:00Z";
            index.ObserveRev(40); Check(index.NextRev()==41,"Lamport counter changed");
            var expected=index.Entries.ToDictionary(pair=>pair.Key,pair=>(JObject)pair.Value.DeepClone());
            index.Save();
            var json=JsonUtil.LoadObject(index.FilePath);
            Check(json["entries"] is JArray && ((JArray)json["entries"]).Count==3,"Entry schema or count changed");
            Check(json["customMetadata"].ToString()=="preserve-me","Unknown metadata lost");
            Check(json["lamport"].Value<int>()==41,"Revision counter lost");
            var restored=new IndexStore(directory,"roundtrip");
            Check(restored.FileCount==2 && restored.TotalBytes==118,"File statistics changed");
            Check(restored.ConflictCount==1 && restored.Conflicts[0]["message"].ToString()=="两份内容都保留","Conflicts lost");
            Check(json["lastScanAt"].Value<DateTime>().ToUniversalTime()==DateTime.Parse("2026-11-13T01:00:00Z").ToUniversalTime() && json["lastSyncAt"].Value<DateTime>().ToUniversalTime()==DateTime.Parse("2026-11-13T01:01:00Z").ToUniversalTime(),"Timestamps changed");
            foreach(var pair in expected) Check(JToken.DeepEquals(JToken.Parse(pair.Value.ToString(Formatting.None)),restored.Get(pair.Key)),"Entry changed: "+pair.Key);
            Check(restored.Get("NEW.TXT")!=null,"Case-insensitive lookup changed");
            restored.Get("new.txt")["size"]=9; restored.Save();
            Check(new IndexStore(directory,"roundtrip").TotalBytes==120,"In-place updates did not persist");
#if STREAMING
            Check(restored.Root["entries"]==null,"A duplicate in-memory entry snapshot remains");
            Check(restored.Entries.Values.All(entry=>entry.Parent==null),"Entries retain a parsed snapshot parent");
            string file=Path.Combine(directory,"failure.json");
            File.WriteAllText(file,"original");
            try { JsonUtil.SaveAtomic(file,delegate(JsonTextWriter writer){writer.WriteStartObject();throw new InvalidOperationException("simulated writer failure");}); }
            catch(InvalidOperationException) { }
            Check(File.ReadAllText(file)=="original","Failed serialization overwrote the existing file");
            Check(Directory.GetFiles(directory,"*.tmp-*").Length==0,"Temporary files were leaked");
#endif
        }
        private static void Benchmark(string directory) {
            Seed(directory,"large",10000);
            var index=new IndexStore(directory,"large");
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            long loaded=GC.GetTotalMemory(true);
            var clock=Stopwatch.StartNew();index.Save();clock.Stop();
            long saved=GC.GetTotalMemory(true);
            Check(new IndexStore(directory,"large").FileCount==10000,"Large index round trip failed");
            var report=JsonUtil.Obj("entries",10000,"managedLoadedMiB",Math.Round(loaded/1048576.0,2),"managedAfterSaveMiB",Math.Round(saved/1048576.0,2),"saveMilliseconds",clock.ElapsedMilliseconds,"duplicateSnapshot",index.Root["entries"]!=null);
            Console.WriteLine(report.ToString(Formatting.None));
        }
        public static void Main(string[] args) {
            try {
                Directory.CreateDirectory(args[0]);RoundTrip(args[0]);Benchmark(args[0]);
                Console.WriteLine("PASS: entries, tombstones, Unicode, metadata, revisions, conflicts and atomic save.");
            } catch(Exception error) { Console.Error.WriteLine(error.GetType().Name+": "+error.Message); Environment.ExitCode=1; }
        }
    }
}
