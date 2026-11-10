using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Orbiters.Toolkit
{
    /// <summary>SHA-256 of bytes, text (UTF-8) and files, as lowercase hex: the one form Orbiters' tools compare and store.</summary>
    public static class Hashing
    {
        private const string Digits = "0123456789abcdef";

        /// <summary>Lowercase hex of <paramref name="bytes"/>.</summary>
        public static string Hex(byte[] bytes)
        {
            if (bytes == null) return null;
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 15];
            }
            return new string(chars);
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text ?? ""));

        public static string Sha256(Stream stream)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        /// <summary>SHA-256 of a file's content, read with others still allowed to write it (as Unity may).</summary>
        public static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20)) return Sha256(stream);
        }
    }
}
