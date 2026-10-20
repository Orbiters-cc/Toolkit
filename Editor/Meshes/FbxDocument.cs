using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// One record of a binary FBX file: its name, its properties and its children. Property values are .NET values, their
    /// FBX type following from theirs: long (L), int (I), short (Y), byte (C), float (F), double (D), string (S), byte[]
    /// (R), and arrays double[] (d), float[] (f), long[] (l), int[] (i), bool[] (b).
    /// </summary>
    public sealed class FbxNode
    {
        public string Name;
        public readonly List<object> Properties = new List<object>();
        public readonly List<FbxNode> Children = new List<FbxNode>();

        public FbxNode(string name, params object[] properties)
        {
            Name = name;
            Properties.AddRange(properties);
        }

        public FbxNode Child(string name) => Children.FirstOrDefault(c => c.Name == name);
        public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

        /// <summary>Adds a child and returns it.</summary>
        public FbxNode Add(string name, params object[] properties)
        {
            var child = new FbxNode(name, properties);
            Children.Add(child);
            return child;
        }

        /// <summary>The first property of the child of that name (an object's array, a layer's mapping...), or null.</summary>
        public T Value<T>(string child) where T : class => Child(child)?.Properties.FirstOrDefault() as T;

        /// <summary>Its id: the first property of an object.</summary>
        public long Id => Properties.Count > 0 && Properties[0] is long id ? id : 0;

        /// <summary>Its class: the third property of an object ("Mesh", "LimbNode", "Skin", "Cluster", "Shape"...).</summary>
        public string Class => Properties.Count > 2 ? Properties[2] as string ?? string.Empty : string.Empty;

        /// <summary>Its name without the type FBX appends ("Body\0\u0001Model" → "Body").</summary>
        public string ObjectName
        {
            get
            {
                string raw = Properties.Count > 1 ? Properties[1] as string ?? string.Empty : string.Empty;
                int split = raw.IndexOf("\0\u0001", StringComparison.Ordinal);
                return split >= 0 ? raw.Substring(0, split) : raw;
            }
        }

        /// <summary>A Properties70 entry ("P") of this object by name, or null.</summary>
        public FbxNode Property70(string name) =>
            Child("Properties70")?.All("P").FirstOrDefault(p => p.Properties.Count > 0 && p.Properties[0] as string == name);
    }

    /// <summary>
    /// A binary FBX file as an editable record tree, written back as binary FBX (the same version, arrays over 128 bytes
    /// compressed as Blender's writer does).
    /// Blender's FBX importer reads what it writes. FbxReader stays the fast read-only path for comparisons.
    /// </summary>
    public sealed class FbxDocument
    {
        /// <summary>The file version (7400, 7500...).</summary>
        public uint Version;
        /// <summary>The top-level records (FBXHeaderExtension, GlobalSettings, Objects, Connections...).</summary>
        public readonly List<FbxNode> Nodes = new List<FbxNode>();

        public FbxNode Top(string name) => Nodes.FirstOrDefault(n => n.Name == name);

        // ---- Reading ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads a binary FBX file. A damaged one (records that end before they start or past their parent, nesting deeper
        /// than files are made, lengths past the end, arrays bigger than they announce) is refused with an
        /// <see cref="InvalidDataException"/>.
        /// </summary>
        public static FbxDocument Read(byte[] bytes)
        {
            if (!FbxReader.IsBinary(bytes)) throw new NotSupportedException("Only binary FBX files can be read (this one is text FBX).");
            var document = new FbxDocument { Version = BitConverter.ToUInt32(bytes, 23) };
            bool wide = document.Version >= 7500;
            var budget = new FbxBinary.Budget(bytes.Length);
            int position = 27;
            while (position < bytes.Length)
            {
                var node = ReadNode(bytes, ref position, wide, bytes.Length, 0, budget);
                if (node == null) break;
                document.Nodes.Add(node);
            }
            return document;
        }

        private static FbxNode ReadNode(byte[] bytes, ref int position, bool wide, long limit, int depth, FbxBinary.Budget budget)
        {
            if (depth > FbxBinary.MaxDepth) throw FbxBinary.Damaged("its records nest too deep");
            if (!FbxBinary.ReadHeader(bytes, ref position, wide, limit, out long end, out long count, out string name)) return null;
            var node = new FbxNode(name);
            for (long i = 0; i < count; i++) node.Properties.Add(ReadProperty(bytes, ref position, end, budget));
            while (position < end)
            {
                var child = ReadNode(bytes, ref position, wide, end, depth + 1, budget);
                if (child == null) break;
                node.Children.Add(child);
            }
            position = (int)end;
            return node;
        }

        private static object ReadProperty(byte[] bytes, ref int position, long limit, FbxBinary.Budget budget)
        {
            FbxBinary.Need(position, 1, limit);
            char type = (char)bytes[position++];
            switch (type)
            {
                case 'Y': FbxBinary.Need(position, 2, limit); position += 2; return BitConverter.ToInt16(bytes, position - 2);
                case 'C': FbxBinary.Need(position, 1, limit); position += 1; return bytes[position - 1];
                case 'I': FbxBinary.Need(position, 4, limit); position += 4; return BitConverter.ToInt32(bytes, position - 4);
                case 'F': FbxBinary.Need(position, 4, limit); position += 4; return BitConverter.ToSingle(bytes, position - 4);
                case 'D': FbxBinary.Need(position, 8, limit); position += 8; return BitConverter.ToDouble(bytes, position - 8);
                case 'L': FbxBinary.Need(position, 8, limit); position += 8; return BitConverter.ToInt64(bytes, position - 8);
                case 'S':
                case 'R':
                {
                    int length = FbxBinary.ReadLength(bytes, ref position, limit);
                    object value = type == 'S' ? Encoding.UTF8.GetString(bytes, position, length) : (object)Slice(bytes, position, length);
                    position += length;
                    return value;
                }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    FbxBinary.ReadArrayHeader(bytes, ref position, limit, type, budget, out int length, out bool compressed, out int stored);
                    int size = length * FbxBinary.ElementSize(type);
                    Array values;
                    switch (type)
                    {
                        case 'f': values = new float[length]; break;
                        case 'd': values = new double[length]; break;
                        case 'l': values = new long[length]; break;
                        case 'i': values = new int[length]; break;
                        default: values = new byte[length]; break;
                    }
                    FbxBinary.Decode(bytes, position, stored, compressed, values, size);
                    position += stored;
                    return values is byte[] flags ? flags.Select(b => b != 0).ToArray() : (object)values;
                }
                default:
                    throw new InvalidDataException("Unknown FBX property type '" + type + "'.");
            }
        }

        private static byte[] Slice(byte[] bytes, int start, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(bytes, start, slice, 0, length);
            return slice;
        }

        // ---- Writing ---------------------------------------------------------------------------------------------------------

        private static readonly byte[] FooterId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] FooterMagic = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };

        /// <summary>The file's bytes, laid out as Blender's FBX writer lays them out (records, end markers, footer).</summary>
        public byte[] Write()
        {
            bool wide = Version >= 7500;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
                writer.Write((byte)0);
                writer.Write((byte)0x1a);
                writer.Write((byte)0);
                writer.Write(Version);
                for (int i = 0; i < Nodes.Count; i++) WriteNode(writer, Nodes[i], wide, i == Nodes.Count - 1);
                writer.Write(new byte[wide ? 25 : 13]);
                writer.Write(FooterId);
                writer.Write(new byte[4]);
                long offset = stream.Position;
                int pad = (int)(((offset + 15) & ~15L) - offset);
                writer.Write(new byte[pad == 0 ? 16 : pad]);
                writer.Write(Version);
                writer.Write(new byte[120]);
                writer.Write(FooterMagic);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteNode(BinaryWriter writer, FbxNode node, bool wide, bool last)
        {
            var stream = writer.BaseStream;
            long start = stream.Position;
            // End offset, property count and property bytes, filled in once known.
            if (wide) { writer.Write(0L); writer.Write((long)node.Properties.Count); writer.Write(0L); }
            else { writer.Write(0); writer.Write(node.Properties.Count); writer.Write(0); }
            var name = Encoding.ASCII.GetBytes(node.Name ?? string.Empty);
            // The name's length is one byte: a longer name would make the rest of the file unreadable.
            if (name.Length > 255) throw new InvalidDataException("An FBX record name is longer than 255 characters: " + node.Name.Substring(0, 40) + "...");
            writer.Write((byte)name.Length);
            writer.Write(name);
            long propertiesStart = stream.Position;
            foreach (var property in node.Properties) WriteProperty(writer, property);
            long propertyBytes = stream.Position - propertiesStart;
            for (int i = 0; i < node.Children.Count; i++) WriteNode(writer, node.Children[i], wide, i == node.Children.Count - 1);
            // A record with children ends with an empty one, as does one with neither properties nor children (but the last).
            if (node.Children.Count > 0 || node.Properties.Count == 0 && !last) writer.Write(new byte[wide ? 25 : 13]);
            long end = stream.Position;
            stream.Position = start;
            if (wide) { writer.Write(end); stream.Position += 8; writer.Write(propertyBytes); }
            else { writer.Write((uint)end); stream.Position += 4; writer.Write((uint)propertyBytes); }
            stream.Position = end;
        }

        private static void WriteProperty(BinaryWriter writer, object value)
        {
            switch (value)
            {
                case short v: writer.Write((byte)'Y'); writer.Write(v); break;
                case byte v: writer.Write((byte)'C'); writer.Write(v); break;
                case bool v: writer.Write((byte)'C'); writer.Write((byte)(v ? 1 : 0)); break;
                case int v: writer.Write((byte)'I'); writer.Write(v); break;
                case float v: writer.Write((byte)'F'); writer.Write(v); break;
                case double v: writer.Write((byte)'D'); writer.Write(v); break;
                case long v: writer.Write((byte)'L'); writer.Write(v); break;
                case string v:
                {
                    var bytes = Encoding.UTF8.GetBytes(v);
                    writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes);
                    break;
                }
                case byte[] v: writer.Write((byte)'R'); writer.Write(v.Length); writer.Write(v); break;
                case double[] v: WriteArray(writer, 'd', v.Length, v, 8); break;
                case float[] v: WriteArray(writer, 'f', v.Length, v, 4); break;
                case long[] v: WriteArray(writer, 'l', v.Length, v, 8); break;
                case int[] v: WriteArray(writer, 'i', v.Length, v, 4); break;
                case bool[] v: WriteArray(writer, 'b', v.Length, v.Select(b => b ? (byte)1 : (byte)0).ToArray(), 1); break;
                default: throw new InvalidDataException("An FBX property can't hold " + (value?.GetType().Name ?? "null") + ".");
            }
        }

        private static void WriteArray(BinaryWriter writer, char type, int length, Array values, int size)
        {
            var bytes = new byte[length * size];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            bool compress = bytes.Length > 128;
            var stored = compress ? Deflate(bytes) : bytes;
            writer.Write((byte)type);
            writer.Write(length);
            writer.Write(compress ? 1 : 0);
            writer.Write(stored.Length);
            writer.Write(stored);
        }

        // zlib: a header, deflate, and the Adler-32 checksum of the data (big-endian).
        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9c);
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) deflate.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (byte value in data)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return output.ToArray();
            }
        }

        // ---- Scene -----------------------------------------------------------------------------------------------------------

        /// <summary>Every object record (models, geometries, deformers, materials...) by id.</summary>
        public Dictionary<long, FbxNode> Objects() =>
            (Top("Objects")?.Children ?? new List<FbxNode>()).Where(n => n.Id != 0).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());

        /// <summary>The object-to-object connections ("C" records): child, parent.</summary>
        public IEnumerable<(long Child, long Parent, FbxNode Record)> Connections() =>
            (Top("Connections")?.All("C") ?? Enumerable.Empty<FbxNode>())
            .Where(c => c.Properties.Count >= 3 && c.Properties[1] is long && c.Properties[2] is long)
            .Select(c => ((long)c.Properties[1], (long)c.Properties[2], c));

        /// <summary>
        /// The file's space to Unity's for points, as Unity's model importer reads it (unit scale to metres, the file's axes
        /// to Y up and X mirrored).
        /// </summary>
        public Matrix4x4 ToUnity()
        {
            var global = Top("GlobalSettings");
            double Setting(string name, double fallback)
            {
                var p = global?.Property70(name);
                if (p == null || p.Properties.Count < 5) return fallback;
                return p.Properties[4] is double d ? d : p.Properties[4] is int i ? i : p.Properties[4] is long l ? l : fallback;
            }
            float scale = (float)(Setting("UnitScaleFactor", 1d) / 100d);
            int up = (int)Setting("UpAxis", 1), upSign = (int)Setting("UpAxisSign", 1);
            int front = (int)Setting("FrontAxis", 2), frontSign = (int)Setting("FrontAxisSign", 1);
            int coord = (int)Setting("CoordAxis", 0), coordSign = (int)Setting("CoordAxisSign", 1);
            var m = Matrix4x4.zero;
            m[0, coord] = -coordSign * scale;
            m[1, up] = upSign * scale;
            m[2, front] = frontSign * scale;
            m[3, 3] = 1f;
            return m;
        }

        /// <summary>A model's matrix in its parent's space, from its Lcl, pre/post rotation and geometric properties.</summary>
        public static Matrix4x4 Local(FbxNode model) => FbxTransform.Local(model);

        /// <summary>A new object id that no record uses yet.</summary>
        public long NewId(HashSet<long> used)
        {
            var random = new System.Random(used.Count * 7919 + 17);
            while (true)
            {
                long id = ((long)random.Next(1 << 30) << 16) ^ random.Next(1 << 16) | 1L << 40;
                if (used.Add(id)) return id;
            }
        }
    }

    /// <summary>FBX model transforms: Lcl translation, rotation (in the model's rotation order), scaling, pre and post rotation.</summary>
    public static class FbxTransform
    {
        public static Vector3 Vector(FbxNode model, string name, Vector3 fallback)
        {
            var p = model.Property70(name);
            if (p == null || p.Properties.Count < 7) return fallback;
            return new Vector3(Number(p.Properties[4]), Number(p.Properties[5]), Number(p.Properties[6]));
        }

        public static int RotationOrder(FbxNode model)
        {
            var p = model.Property70("RotationOrder");
            return p != null && p.Properties.Count > 4 ? (int)Number(p.Properties[4]) : 0;
        }

        /// <summary>The model in its parent's space (geometric offsets left out: they move only its geometry).</summary>
        public static Matrix4x4 Local(FbxNode model)
        {
            var t = Vector(model, "Lcl Translation", Vector3.zero);
            var r = Vector(model, "Lcl Rotation", Vector3.zero);
            var s = Vector(model, "Lcl Scaling", Vector3.one);
            var pre = Vector(model, "PreRotation", Vector3.zero);
            var post = Vector(model, "PostRotation", Vector3.zero);
            return Matrix4x4.Translate(t) * Euler(pre, 0) * Euler(r, RotationOrder(model)) * Euler(post, 0).inverse * Matrix4x4.Scale(s);
        }

        /// <summary>The model's geometric offset: where its geometry sits in its own space.</summary>
        public static Matrix4x4 Geometric(FbxNode model) =>
            Matrix4x4.Translate(Vector(model, "GeometricTranslation", Vector3.zero)) * Euler(Vector(model, "GeometricRotation", Vector3.zero), 0) *
            Matrix4x4.Scale(Vector(model, "GeometricScaling", Vector3.one));

        /// <summary>Euler angles in degrees, applied in the FBX rotation order (0 = XYZ: X first, then Y, then Z).</summary>
        public static Matrix4x4 Euler(Vector3 degrees, int order)
        {
            Matrix4x4 X = Axis(0, degrees.x), Y = Axis(1, degrees.y), Z = Axis(2, degrees.z);
            switch (order)
            {
                case 1: return Y * Z * X;
                case 2: return X * Z * Y;
                case 3: return Z * X * Y;
                case 4: return Y * X * Z;
                case 5: return X * Y * Z;
                default: return Z * Y * X;
            }
        }

        /// <summary>The XYZ Euler angles (degrees, X applied first) of a rotation matrix: the inverse of Euler(..., 0).</summary>
        public static Vector3 EulerXyz(Matrix4x4 m)
        {
            // m = Rz(γ) Ry(β) Rx(α): m20 = -sin β, m21 = cos β sin α, m22 = cos β cos α, m10 = sin γ cos β, m00 = cos γ cos β.
            double sy = -m.m20;
            sy = Math.Max(-1d, Math.Min(1d, sy));
            double beta = Math.Asin(sy), alpha, gamma;
            if (Math.Abs(sy) < 0.999999d)
            {
                alpha = Math.Atan2(m.m21, m.m22);
                gamma = Math.Atan2(m.m10, m.m00);
            }
            else
            {
                // Gimbal lock: X and Z turn about the same axis; it all goes to X.
                gamma = 0d;
                alpha = Math.Atan2(-m.m12, m.m11);
            }
            const double degrees = 180d / Math.PI;
            return new Vector3((float)(alpha * degrees), (float)(beta * degrees), (float)(gamma * degrees));
        }

        private static Matrix4x4 Axis(int axis, float degrees)
        {
            double radians = degrees * Math.PI / 180d;
            float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
            var m = Matrix4x4.identity;
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            m[a, a] = c; m[a, b] = -s;
            m[b, a] = s; m[b, b] = c;
            return m;
        }

        public static float Number(object value) => value is double d ? (float)d : value is float f ? f : value is long l ? l : value is int i ? i : 0f;

        /// <summary>A row-major-in-memory FBX matrix (16 doubles, column after column) as a Unity matrix, and back.</summary>
        public static Matrix4x4 FromDoubles(double[] values)
        {
            var m = new Matrix4x4();
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                    m[row, column] = (float)values[column * 4 + row];
            return m;
        }

        public static double[] ToDoubles(Matrix4x4 m)
        {
            var values = new double[16];
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                    values[column * 4 + row] = m[row, column];
            return values;
        }
    }
}
