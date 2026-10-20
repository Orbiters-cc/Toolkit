using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// The record layout of binary FBX files and the bounds every reader of them keeps to (<see cref="FbxDocument"/>,
    /// <see cref="FbxReader"/>): a record ends after its header and inside its parent, nesting stops at
    /// <see cref="MaxDepth"/>, every length stays inside the file, and arrays decode to exactly the size they announce,
    /// within <see cref="MaxArrayBytes"/> each and a budget for the whole file. A damaged or hostile file is refused with
    /// an <see cref="InvalidDataException"/> instead of looping, overflowing the stack or filling the memory.
    /// </summary>
    internal static class FbxBinary
    {
        /// <summary>How deep records may nest (files that applications write nest less than ten deep).</summary>
        public const int MaxDepth = 64;
        /// <summary>The most one array may decode to.</summary>
        public const long MaxArrayBytes = 512L * 1024 * 1024;

        /// <summary>What the arrays of one file may decode to in all: 64 times the file, at least 256 MB, at most 2 GB.</summary>
        public sealed class Budget
        {
            private long left;
            public Budget(long fileBytes) { left = Math.Min(2L << 30, Math.Max(256L << 20, 64L * fileBytes)); }

            public void Take(long bytes)
            {
                left -= bytes;
                if (left < 0) throw Damaged("its arrays announce more data than such a file can hold");
            }
        }

        public static InvalidDataException Damaged(string what) => new InvalidDataException("The FBX file is damaged: " + what + ".");

        /// <summary>Refuses <paramref name="count"/> bytes at <paramref name="position"/> that do not lie before <paramref name="limit"/>.</summary>
        public static void Need(long position, long count, long limit)
        {
            if (count < 0 || position < 0 || position + count > limit) throw Damaged("a record runs past its end");
        }

        /// <summary>
        /// Reads a record's header at <paramref name="position"/>: false at the empty record that ends a list of records.
        /// <paramref name="end"/> lies after the header and not past <paramref name="limit"/> (its parent's end, or the file's).
        /// </summary>
        public static bool ReadHeader(byte[] bytes, ref int position, bool wide, long limit, out long end, out long count, out string name)
        {
            int size = wide ? 25 : 13;
            Need(position, size, limit);
            if (wide)
            {
                ulong rawEnd = BitConverter.ToUInt64(bytes, position), rawCount = BitConverter.ToUInt64(bytes, position + 8);
                end = rawEnd > long.MaxValue ? -1 : (long)rawEnd;
                count = rawCount > long.MaxValue ? -1 : (long)rawCount;
            }
            else
            {
                end = BitConverter.ToUInt32(bytes, position);
                count = BitConverter.ToUInt32(bytes, position + 4);
            }
            int nameLength = bytes[position + size - 1];
            position += size;
            name = null;
            if (end == 0) return false;
            Need(position, nameLength, limit);
            name = Encoding.ASCII.GetString(bytes, position, nameLength);
            position += nameLength;
            // Each property takes two bytes at least.
            if (end < position || end > limit || count < 0 || count > (end - position) / 2) throw Damaged("a record's end or size is wrong");
            return true;
        }

        /// <summary>Reads a length-prefixed string or raw property's length; its bytes start at <paramref name="position"/>.</summary>
        public static int ReadLength(byte[] bytes, ref int position, long limit)
        {
            Need(position, 4, limit);
            int length = BitConverter.ToInt32(bytes, position);
            position += 4;
            Need(position, length, limit);
            return length;
        }

        /// <summary>The size of an array property's element ('f', 'd', 'l', 'i', 'b').</summary>
        public static int ElementSize(char type) => type == 'd' || type == 'l' ? 8 : type == 'b' ? 1 : 4;

        /// <summary>
        /// Reads an array property's header: its length, whether it is compressed, and its stored bytes (which start at
        /// <paramref name="position"/> once read), all checked; its decoded size is taken from <paramref name="budget"/>.
        /// </summary>
        public static void ReadArrayHeader(byte[] bytes, ref int position, long limit, char type, Budget budget, out int length, out bool compressed, out int stored)
        {
            Need(position, 12, limit);
            length = BitConverter.ToInt32(bytes, position);
            int encoding = BitConverter.ToInt32(bytes, position + 4);
            stored = BitConverter.ToInt32(bytes, position + 8);
            position += 12;
            long size = (long)length * ElementSize(type);
            if (length < 0 || size > MaxArrayBytes || encoding != 0 && encoding != 1) throw Damaged("an array's header is wrong");
            compressed = encoding == 1;
            Need(position, stored, limit);
            if (compressed ? stored < 2 && size > 0 : stored != size) throw Damaged("an array's size is wrong");
            budget.Take(size);
        }

        /// <summary>
        /// The zlib data at <paramref name="start"/> (a 2-byte header, then deflate; the Adler-32 checksum at the end is left
        /// unread), decoded to exactly <paramref name="expected"/> bytes: never more, and refused when it holds fewer.
        /// </summary>
        public static byte[] Inflate(byte[] bytes, int start, int stored, int expected)
        {
            var output = new byte[expected];
            Decode(bytes, start, stored, true, output, expected);
            return output;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> (an array of primitives) with an array property's <paramref name="byteCount"/>
        /// bytes, stored at <paramref name="start"/> plainly or as zlib data: decoded straight into it, so a big array never
        /// needs a second copy of its bytes.
        /// </summary>
        public static void Decode(byte[] bytes, int start, int stored, bool compressed, Array destination, int byteCount)
        {
            if (byteCount == 0) return;
            if (!compressed)
            {
                Buffer.BlockCopy(bytes, start, destination, 0, byteCount);
                return;
            }
            if (stored < 2) throw Damaged("an array's compressed data is missing");
            var chunk = new byte[Math.Min(byteCount, 1 << 16)];
            using (var input = new MemoryStream(bytes, start + 2, stored - 2))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int read = 0;
                try
                {
                    while (read < byteCount)
                    {
                        int n = inflate.Read(chunk, 0, Math.Min(chunk.Length, byteCount - read));
                        if (n <= 0) break;
                        Buffer.BlockCopy(chunk, 0, destination, read, n);
                        read += n;
                    }
                }
                catch (IOException) { throw Damaged("an array's compressed data is corrupt"); }
                if (read != byteCount) throw Damaged("an array holds less than it announces");
            }
        }
    }
}
