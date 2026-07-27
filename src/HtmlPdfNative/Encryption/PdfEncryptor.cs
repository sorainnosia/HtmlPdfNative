using System;
using System.IO;
using System.Security.Cryptography;

namespace HtmlPdfNative.Encryption
{
    /// <summary>
    /// PDF standard security handler, V4/R4 with AES-128 (AESV2). Computes the /O, /U entries and the
    /// document key from the user/owner passwords (Algorithms 2/3/5, using MD5 + RC4), and encrypts each
    /// string/stream with a per-object AES-CBC key. Port of <c>src/encryption.rs</c>.
    /// </summary>
    public sealed class PdfEncryptor
    {
        private const int KeyLen = 16; // 128-bit
        private static readonly byte[] Pad =
        {
            0x28,0xBF,0x4E,0x5E,0x4E,0x75,0x8A,0x41,0x64,0x00,0x4E,0x56,0xFF,0xFA,0x01,0x08,
            0x2E,0x2E,0x00,0xB6,0xD0,0x68,0x3E,0x80,0x2F,0x0C,0xA9,0xFE,0x64,0x53,0x69,0x7A
        };

        private readonly byte[] _key;   // document encryption key
        public byte[] O { get; }
        public byte[] U { get; }
        public int Permissions { get; }
        public byte[] IdBytes { get; }

        public PdfEncryptor(string? userPassword, string? ownerPassword, byte[] idBytes, int permissions = -4)
        {
            IdBytes = idBytes;
            Permissions = permissions;
            string user = userPassword ?? "";
            string owner = string.IsNullOrEmpty(ownerPassword) ? user : ownerPassword!;

            O = ComputeO(user, owner);
            _key = ComputeKey(user, O, permissions, idBytes);
            U = ComputeU(_key, idBytes);
        }

        /// <summary>Encrypt (or decrypt — AES-CBC is symmetric setup) a string/stream for object N.
        /// Returns IV(16) followed by the AES-CBC ciphertext, per the AESV2 crypt filter.</summary>
        public byte[] Encrypt(int objNum, int gen, byte[] data)
        {
            byte[] objKey = ObjectKey(objNum, gen);
            using (var aes = Aes.Create())
            {
                aes.KeySize = 128; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = objKey;
                byte[] iv = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] ct = enc.TransformFinalBlock(data, 0, data.Length);
                    var outBuf = new byte[16 + ct.Length];
                    Buffer.BlockCopy(iv, 0, outBuf, 0, 16);
                    Buffer.BlockCopy(ct, 0, outBuf, 16, ct.Length);
                    return outBuf;
                }
            }
        }

        /// <summary>The /Encrypt dictionary body (without object wrapper). Never itself encrypted.</summary>
        public string EncryptDict()
        {
            return "<< /Filter /Standard /V 4 /R 4 /Length 128" +
                   " /CF << /StdCF << /CFM /AESV2 /Length 16 /AuthEvent /DocOpen >> >>" +
                   " /StmF /StdCF /StrF /StdCF" +
                   " /O <" + Hex(O) + "> /U <" + Hex(U) + "> /P " + Permissions + " >>";
        }

        private byte[] ObjectKey(int objNum, int gen)
        {
            using (var md5 = MD5.Create())
            {
                var buf = new byte[_key.Length + 5 + 4];
                Buffer.BlockCopy(_key, 0, buf, 0, _key.Length);
                int p = _key.Length;
                buf[p] = (byte)(objNum & 0xFF); buf[p + 1] = (byte)((objNum >> 8) & 0xFF); buf[p + 2] = (byte)((objNum >> 16) & 0xFF);
                buf[p + 3] = (byte)(gen & 0xFF); buf[p + 4] = (byte)((gen >> 8) & 0xFF);
                // AESV2 salt "sAlT"
                buf[p + 5] = 0x73; buf[p + 6] = 0x41; buf[p + 7] = 0x6C; buf[p + 8] = 0x54;
                byte[] h = md5.ComputeHash(buf);
                int n = Math.Min(_key.Length + 5, 16);
                var key = new byte[n];
                Buffer.BlockCopy(h, 0, key, 0, n);
                return key;
            }
        }

        private static byte[] ComputeO(string user, string owner)
        {
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(PadPassword(owner));
                for (int i = 0; i < 50; i++) h = md5.ComputeHash(Take(h, KeyLen));
                byte[] rc4Key = Take(h, KeyLen);
                byte[] enc = Rc4(PadPassword(user), rc4Key);
                for (int i = 1; i <= 19; i++) enc = Rc4(enc, XorKey(rc4Key, i));
                return enc; // 32 bytes
            }
        }

        private static byte[] ComputeKey(string user, byte[] o, int p, byte[] id)
        {
            using (var md5 = MD5.Create())
            using (var ms = new MemoryStream())
            {
                var up = PadPassword(user);
                ms.Write(up, 0, up.Length);
                ms.Write(o, 0, o.Length);
                ms.WriteByte((byte)(p & 0xFF)); ms.WriteByte((byte)((p >> 8) & 0xFF));
                ms.WriteByte((byte)((p >> 16) & 0xFF)); ms.WriteByte((byte)((p >> 24) & 0xFF));
                ms.Write(id, 0, id.Length);
                byte[] h = md5.ComputeHash(ms.ToArray());
                for (int i = 0; i < 50; i++) h = md5.ComputeHash(Take(h, KeyLen));
                return Take(h, KeyLen);
            }
        }

        private static byte[] ComputeU(byte[] key, byte[] id)
        {
            using (var md5 = MD5.Create())
            using (var ms = new MemoryStream())
            {
                ms.Write(Pad, 0, Pad.Length);
                ms.Write(id, 0, id.Length);
                byte[] h = md5.ComputeHash(ms.ToArray()); // 16
                byte[] enc = Rc4(h, key);
                for (int i = 1; i <= 19; i++) enc = Rc4(enc, XorKey(key, i));
                var u = new byte[32];
                Buffer.BlockCopy(enc, 0, u, 0, 16); // pad with zeros
                return u;
            }
        }

        private static byte[] PadPassword(string pw)
        {
            var pwb = new byte[pw.Length];
            for (int i = 0; i < pw.Length; i++) pwb[i] = (byte)(pw[i] & 0xFF);
            var buf = new byte[32];
            int n = Math.Min(32, pwb.Length);
            Buffer.BlockCopy(pwb, 0, buf, 0, n);
            Buffer.BlockCopy(Pad, 0, buf, n, 32 - n);
            return buf;
        }

        private static byte[] XorKey(byte[] key, int i)
        {
            var k = new byte[key.Length];
            for (int j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ i);
            return k;
        }

        private static byte[] Take(byte[] a, int n) { var b = new byte[n]; Buffer.BlockCopy(a, 0, b, 0, n); return b; }

        /// <summary>RC4 stream cipher (used only for the O/U/key setup, not document data).</summary>
        public static byte[] Rc4(byte[] data, byte[] key)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var outBuf = new byte[data.Length];
            int a = 0, b = 0;
            for (int k = 0; k < data.Length; k++)
            {
                a = (a + 1) & 0xFF; b = (b + s[a]) & 0xFF;
                (s[a], s[b]) = (s[b], s[a]);
                outBuf[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 0xFF]);
            }
            return outBuf;
        }

        public static string Hex(byte[] b)
        {
            var c = new char[b.Length * 2];
            const string H = "0123456789ABCDEF";
            for (int i = 0; i < b.Length; i++) { c[i * 2] = H[b[i] >> 4]; c[i * 2 + 1] = H[b[i] & 0xF]; }
            return new string(c);
        }

        /// <summary>Generate a random 16-byte document /ID.</summary>
        public static byte[] NewId()
        {
            var id = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(id);
            return id;
        }
    }
}
