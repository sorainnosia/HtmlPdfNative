using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HtmlPdfNative.PdfWriter
{
    /// <summary>
    /// Minimal low-level PDF document writer. Vendored .NET port of the Rust crate <c>pdf-writer</c>:
    /// allocates indirect objects, lets callers write dictionaries/streams, and serializes a valid
    /// PDF 1.7 file with a cross-reference table and trailer.
    /// </summary>
    public sealed class PdfDocument
    {
        private sealed class Entry { public bool IsStream; public string Body = ""; public byte[]? Data; }
        private readonly List<Entry?> _objects = new List<Entry?> { null }; // 1-based; index 0 unused

        /// <summary>Reserve an object id (for forward references). Returns a 1-based object number.</summary>
        public int Allocate()
        {
            _objects.Add(null);
            return _objects.Count - 1;
        }

        /// <summary>Set the raw body of an object (everything between "N 0 obj" and "endobj").</summary>
        public void Set(int id, string body) => _objects[id] = new Entry { IsStream = false, Body = body };

        /// <summary>Set an object whose body is a stream: an inner dictionary (no &lt;&lt; &gt;&gt;) plus binary data.</summary>
        public void SetStream(int id, string dict, byte[] streamData)
            => _objects[id] = new Entry { IsStream = true, Body = dict, Data = streamData };

        /// <summary>Serialize the whole document. <paramref name="rootId"/> is the /Catalog object.</summary>
        public byte[] Build(int rootId) => Build(rootId, -1, null, null);

        /// <summary>
        /// Serialize, optionally encrypting. <paramref name="encryptObjId"/> is the /Encrypt object id
        /// (never itself encrypted); <paramref name="idHex"/> is the document /ID; <paramref name="encrypt"/>
        /// encrypts a string/stream for (objNum, gen=0).
        /// </summary>
        public byte[] Build(int rootId, int encryptObjId, string? idHex, Func<int, int, byte[], byte[]>? encrypt)
        {
            using (var ms = new MemoryStream())
            {
                void W(string s) { var b = Latin1(s); ms.Write(b, 0, b.Length); }
                void WB(byte[] b) { ms.Write(b, 0, b.Length); }

                W("%PDF-1.7\n");
                W("%âãÏÓ\n"); // binary marker so tools treat it as binary

                var offsets = new long[_objects.Count];
                for (int id = 1; id < _objects.Count; id++)
                {
                    var e = _objects[id];
                    if (e == null) throw new InvalidOperationException($"object {id} allocated but never set");
                    offsets[id] = ms.Position;
                    W(id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");

                    bool doEnc = encrypt != null && id != encryptObjId;
                    if (e.IsStream)
                    {
                        byte[] data = e.Data ?? Array.Empty<byte>();
                        string dict = e.Body;
                        if (doEnc) { data = encrypt!(id, 0, data); dict = EncryptStringLiterals(dict, id, encrypt!); }
                        W("<< " + dict + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
                        WB(data);
                        W("\nendstream");
                    }
                    else
                    {
                        string body = doEnc ? EncryptStringLiterals(e.Body, id, encrypt!) : e.Body;
                        WB(Latin1(body));
                    }
                    W("\nendobj\n");
                }

                long xref = ms.Position;
                int count = _objects.Count; // includes the free object 0
                W("xref\n");
                W("0 " + count.ToString(CultureInfo.InvariantCulture) + "\n");
                W("0000000000 65535 f \n");
                for (int id = 1; id < count; id++)
                    W(offsets[id].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

                var trailer = new StringBuilder();
                trailer.Append("trailer\n<< /Size ").Append(count).Append(" /Root ").Append(rootId).Append(" 0 R");
                if (encryptObjId > 0) trailer.Append(" /Encrypt ").Append(encryptObjId).Append(" 0 R");
                if (idHex != null) trailer.Append(" /ID [<").Append(idHex).Append("> <").Append(idHex).Append(">]");
                trailer.Append(" >>\n");
                W(trailer.ToString());
                W("startxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n");
                W("%%EOF");
                return ms.ToArray();
            }
        }

        // Windows-1252 (WinAnsi) high-range mappings for Unicode code points that are NOT Latin-1,
        // so bullets/dashes/curly-quotes/ellipsis encode to the right byte for /WinAnsiEncoding fonts.
        private static readonly Dictionary<int, byte> Win1252 = new Dictionary<int, byte>
        {
            {0x20AC,0x80},{0x201A,0x82},{0x0192,0x83},{0x201E,0x84},{0x2026,0x85},{0x2020,0x86},
            {0x2021,0x87},{0x02C6,0x88},{0x2030,0x89},{0x0160,0x8A},{0x2039,0x8B},{0x0152,0x8C},
            {0x017D,0x8E},{0x2018,0x91},{0x2019,0x92},{0x201C,0x93},{0x201D,0x94},{0x2022,0x95},
            {0x2013,0x96},{0x2014,0x97},{0x02DC,0x98},{0x2122,0x99},{0x0161,0x9A},{0x203A,0x9B},
            {0x0153,0x9C},{0x017E,0x9E},{0x0178,0x9F},
        };

        /// <summary>
        /// Replace every top-level <c>(...)</c> literal string in a serialized object with an encrypted
        /// hex string <c>&lt;...&gt;</c>. Handles the simple literals this writer emits (no nested escapes
        /// beyond <c>\( \) \\</c>). Used by the encryption pass so all strings are encrypted per spec.
        /// </summary>
        private static string EncryptStringLiterals(string body, int id, Func<int, int, byte[], byte[]> encrypt)
        {
            var sb = new StringBuilder(body.Length + 16);
            int i = 0, n = body.Length;
            while (i < n)
            {
                char c = body[i];
                if (c == '(')
                {
                    // Find matching ')', collecting raw bytes (unescaping \( \) \\).
                    var raw = new List<byte>();
                    int depth = 1; i++;
                    while (i < n && depth > 0)
                    {
                        char d = body[i];
                        if (d == '\\' && i + 1 < n)
                        {
                            char e = body[i + 1];
                            if (e == '(' || e == ')' || e == '\\') { raw.Add((byte)e); i += 2; continue; }
                            if (e == 'n') { raw.Add((byte)'\n'); i += 2; continue; }
                            if (e == 'r') { raw.Add((byte)'\r'); i += 2; continue; }
                            if (e == 't') { raw.Add((byte)'\t'); i += 2; continue; }
                            raw.Add((byte)e); i += 2; continue;
                        }
                        if (d == '(') { depth++; raw.Add((byte)'('); i++; continue; }
                        if (d == ')') { depth--; if (depth == 0) { i++; break; } raw.Add((byte)')'); i++; continue; }
                        raw.Add((byte)(d <= 0xFF ? d : '?')); i++;
                    }
                    byte[] enc = encrypt(id, 0, raw.ToArray());
                    sb.Append('<').Append(HexBytes(enc)).Append('>');
                }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        private static string HexBytes(byte[] b)
        {
            const string H = "0123456789ABCDEF";
            var c = new char[b.Length * 2];
            for (int i = 0; i < b.Length; i++) { c[i * 2] = H[b[i] >> 4]; c[i * 2 + 1] = H[b[i] & 0xF]; }
            return new string(c);
        }

        /// <summary>WinAnsi (CP1252) byte encoding for PDF syntax + string literals. Unmappable -&gt; '?'.</summary>
        public static byte[] Latin1(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c <= 0x7F || (c >= 0xA0 && c <= 0xFF)) b[i] = (byte)c;
                else if (Win1252.TryGetValue(c, out var wb)) b[i] = wb;
                else b[i] = (byte)'?';
            }
            return b;
        }

        /// <summary>Escape a string for a PDF literal string "( ... )".</summary>
        public static string EscapeLiteral(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break; // WinAnsi mapping happens in Latin1()
                }
            }
            return sb.ToString();
        }
    }
}
