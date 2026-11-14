using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TwinSync.Native
{
    internal static class TextPreview
    {
        internal static JObject Read(Stream input, long offset, string expectedStamp, string stamp)
        {
            var length = input.Length;
            if (offset < 0 || offset > length) throw new IOException("文本分段位置无效。");
            if (!string.IsNullOrEmpty(expectedStamp) && expectedStamp != stamp) throw new IOException("文件已变化，请重新打开对比。");
            var head = new byte[4]; input.Position = 0; var count = input.Read(head, 0, head.Length);
            Encoding encoding = new UTF8Encoding(false, true); var bom = 0;
            if (count >= 3 && head[0] == 239 && head[1] == 187 && head[2] == 191) bom = 3;
            else if (count >= 2 && head[0] == 255 && head[1] == 254) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
            else if (count >= 2 && head[0] == 254 && head[1] == 255) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
            var start = offset == 0 ? bom : offset;
            if (start < bom || encoding is UnicodeEncoding && (start - bom) % 2 != 0) throw new IOException("文本分段位置无效。");
            input.Position = start;
            var bytes = new byte[(int)Math.Min(65536, length - start)]; var read = 0;
            while (read < bytes.Length) { var n = input.Read(bytes, read, bytes.Length - read); if (n == 0) break; read += n; }
            string text = null;
            for (var trim = 0; trim <= 3 && read - trim >= 0; trim++)
                try { text = encoding.GetString(bytes, 0, read - trim); break; } catch (DecoderFallbackException) { if (start + read == length) break; }
            if (text == null || text.IndexOf('\0') >= 0) return JsonUtil.Obj("binary", true, "totalBytes", length, "stamp", stamp, "eof", true);
            var chars = Math.Min(text.Length, 32768); var lines = 0;
            for (var i = 0; i < chars; i++) if (text[i] == '\n' && ++lines == 200) { chars = i + 1; break; }
            if (chars > 0 && char.IsHighSurrogate(text[chars - 1])) chars--;
            text = text.Substring(0, chars);
            var next = start + encoding.GetByteCount(text);
            return JsonUtil.Obj("binary", false, "text", text, "offset", offset, "nextOffset", next, "totalBytes", length, "eof", next >= length, "stamp", stamp);
        }
        internal static JObject FilePreview(string path, long offset, string stamp)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Read(input, offset, stamp, input.Length + ":" + File.GetLastWriteTimeUtc(path).Ticks);
        }
    }
}
