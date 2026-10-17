using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.LiveLink
{
    /// <summary>
    /// The wire format of a live link between Unity and another application (Blender, Substance 3D Painter...). Every frame
    /// is, little-endian:
    /// <code>
    ///   u32 magic, u8 version, u8 type, u16 token length, u32 payload length, then the token (UTF-8) and the payload
    /// </code>
    /// Each link has its own magic, version and frame types. Every frame carries the session's token: a frame with another
    /// token (or that doesn't hold together) closes the connection, and nothing it sent is used.
    /// </summary>
    public sealed class LiveLinkFormat
    {
        public const int HeaderBytes = 12;
        public const int MaxTokenBytes = 256;

        public uint Magic { get; }
        public byte Version { get; }
        public int MaxPayloadBytes { get; }
        private readonly Func<byte, bool> knownType;

        public LiveLinkFormat(uint magic, byte version, int maxPayloadBytes, Func<byte, bool> knownType)
        {
            Magic = magic; Version = version; MaxPayloadBytes = maxPayloadBytes;
            this.knownType = knownType ?? (_ => true);
        }

        public byte[] Encode(byte type, string token, byte[] payload)
        {
            byte[] tokenBytes = Encoding.UTF8.GetBytes(token ?? string.Empty);
            payload ??= Array.Empty<byte>();
            if (tokenBytes.Length > MaxTokenBytes) throw new ArgumentException("The token is too long.");
            if (payload.Length > MaxPayloadBytes) throw new ArgumentException("The frame is too big.");
            var data = new byte[HeaderBytes + tokenBytes.Length + payload.Length];
            BitConverter.GetBytes(Magic).CopyTo(data, 0);
            data[4] = Version;
            data[5] = type;
            BitConverter.GetBytes((ushort)tokenBytes.Length).CopyTo(data, 6);
            BitConverter.GetBytes(payload.Length).CopyTo(data, 8);
            tokenBytes.CopyTo(data, HeaderBytes);
            payload.CopyTo(data, HeaderBytes + tokenBytes.Length);
            return data;
        }

        /// <summary>Reads the 12-byte header: false when it is not a frame of this format (or announces too much).</summary>
        public bool TryReadHeader(byte[] header, out byte type, out int tokenLength, out int payloadLength)
        {
            type = 0;
            tokenLength = payloadLength = 0;
            if (header == null || header.Length < HeaderBytes) return false;
            if (BitConverter.ToUInt32(header, 0) != Magic || header[4] != Version) return false;
            type = header[5];
            tokenLength = BitConverter.ToUInt16(header, 6);
            payloadLength = BitConverter.ToInt32(header, 8);
            return knownType(type) && tokenLength <= MaxTokenBytes && payloadLength >= 0 && payloadLength <= MaxPayloadBytes;
        }

        /// <summary>Compares tokens in constant time (a wrong token never tells how much of it was right).</summary>
        public static bool SameToken(string a, string b)
        {
            if (a == null || b == null) return false;
            int difference = a.Length ^ b.Length;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                difference |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
            return difference == 0;
        }
    }

    /// <summary>One frame received by a <see cref="LiveLinkServer"/>.</summary>
    public sealed class LiveLinkFrame
    {
        /// <summary>The connection it came from (see <see cref="LiveLinkServer.ConnectionChanged"/>).</summary>
        public int Connection;
        public byte Type;
        public string Token = string.Empty;
        public byte[] Payload = Array.Empty<byte>();
        /// <summary>What the link's <c>accept</c> check made of the payload (read on the connection's thread).</summary>
        public object Decoded;
    }

    /// <summary>
    /// Unity's end of a live link: a listener on 127.0.0.1 whose connections are read on background threads, their frames
    /// handed to the editor's main thread through <see cref="EditorApplication.update"/>, in the order they came (with
    /// connections and disconnections). Frames waiting with the same collapse key (the positions of one mesh, the newest
    /// image of one texture) collapse to the newest. Closed before script reloads.
    /// </summary>
    public sealed class LiveLinkServer : IDisposable
    {
        private sealed class Connection
        {
            public int Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
            // Replaced by a newer connection (single-client links): it leaves without a word.
            public volatile bool Superseded;
        }

        // A connection or a disconnection (Frame null), or a frame.
        private readonly struct Item
        {
            public readonly int Connection;
            public readonly bool Up;
            public readonly LiveLinkFrame Frame;
            public Item(int connection, bool up, LiveLinkFrame frame) { Connection = connection; Up = up; Frame = frame; }
        }

        private readonly LiveLinkFormat format;
        private readonly string token;
        private readonly bool singleClient;
        private readonly byte? closingType;
        private readonly Func<LiveLinkFrame, bool> accept;
        private readonly Func<LiveLinkFrame, string> collapseKey;
        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly Dictionary<int, Connection> connections = new Dictionary<int, Connection>();
        private readonly ConcurrentQueue<Item> items = new ConcurrentQueue<Item>();
        private readonly ConcurrentDictionary<(int, string), LiveLinkFrame> latest = new ConcurrentDictionary<(int, string), LiveLinkFrame>();
        private int nextId;
        private volatile bool disposed;

        /// <summary>A frame reached the main thread (already checked: this session's token, a payload that holds together).</summary>
        public event Action<LiveLinkFrame> Received;
        /// <summary>A connection opened (true) or closed (false), on the main thread.</summary>
        public event Action<int, bool> ConnectionChanged;

        public int Port { get; }
        public bool Connected { get { lock (connections) return connections.Count > 0; } }
        public int[] Connections { get { lock (connections) return connections.Keys.ToArray(); } }

        /// <param name="port">0 for any free port; a port to listen on again after a script reload (any other when taken).</param>
        /// <param name="singleClient">One connection at a time: a new one replaces the previous (an app reconnecting after a crash).</param>
        /// <param name="accept">Checks (and decodes into <see cref="LiveLinkFrame.Decoded"/>) a frame on its connection's thread;
        /// false closes the connection.</param>
        /// <param name="collapseKey">Frames waiting with the same key (per connection) collapse to the newest; null: none do.</param>
        /// <param name="closingType">The frame type that says goodbye: the connection closes after it, and it is sent on dispose.</param>
        public LiveLinkServer(LiveLinkFormat format, string token, int port = 0, bool singleClient = false, Func<LiveLinkFrame, bool> accept = null,
            Func<LiveLinkFrame, string> collapseKey = null, byte? closingType = null, string threadName = "Orbiters live link")
        {
            this.format = format ?? throw new ArgumentNullException(nameof(format));
            this.token = token ?? throw new ArgumentNullException(nameof(token));
            this.singleClient = singleClient;
            this.accept = accept;
            this.collapseKey = collapseKey;
            this.closingType = closingType;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (SocketException) when (port != 0)
            {
                // Taken meanwhile: any free port (the endpoint file says which).
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = threadName };
            acceptThread.Start();
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
        }

        /// <summary>Sends a frame on one connection; false when it is gone or the send failed.</summary>
        public bool Send(int connection, byte type, byte[] payload = null)
        {
            Connection target;
            lock (connections) connections.TryGetValue(connection, out target);
            return target != null && Write(target, format.Encode(type, token, payload));
        }

        /// <summary>Sends a frame on every connection; how many it reached.</summary>
        public int Broadcast(byte type, byte[] payload = null)
        {
            Connection[] all;
            lock (connections) all = connections.Values.ToArray();
            if (all.Length == 0) return 0;
            byte[] data = format.Encode(type, token, payload);
            return all.Count(c => Write(c, data));
        }

        /// <summary>Closes one connection (its disconnection reaches the main thread as any other).</summary>
        public void Disconnect(int connection)
        {
            Connection target;
            lock (connections) connections.TryGetValue(connection, out target);
            try { target?.Client.Close(); }
            catch (Exception) { }
        }

        private static bool Write(Connection connection, byte[] data)
        {
            try
            {
                lock (connection.SendLock) connection.Stream.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Writes a small file whole or not at all (an endpoint the other application reads while it may change).</summary>
        public static void WriteAtomically(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                TcpClient accepted;
                try { accepted = listener.AcceptTcpClient(); }
                catch (Exception) when (disposed) { return; }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }

                accepted.NoDelay = true;
                var connection = new Connection { Id = Interlocked.Increment(ref nextId), Client = accepted, Stream = accepted.GetStream() };
                Connection[] replaced;
                lock (connections)
                {
                    replaced = singleClient ? connections.Values.ToArray() : Array.Empty<Connection>();
                    foreach (var old in replaced) { old.Superseded = true; connections.Remove(old.Id); }
                    connections[connection.Id] = connection;
                }
                foreach (var old in replaced)
                    try { old.Client.Close(); }
                    catch (Exception) { }
                items.Enqueue(new Item(connection.Id, true, null));
                var reader = new Thread(() => ReadLoop(connection)) { IsBackground = true, Name = acceptThread.Name + " reader" };
                reader.Start();
            }
        }

        private void ReadLoop(Connection connection)
        {
            var header = new byte[LiveLinkFormat.HeaderBytes];
            try
            {
                while (!disposed && connection.Client.Connected)
                {
                    if (!ReadExactly(connection.Stream, header)) return;
                    if (!format.TryReadHeader(header, out byte type, out int tokenLength, out int payloadLength)) return;
                    var tokenBytes = new byte[tokenLength];
                    if (!ReadExactly(connection.Stream, tokenBytes)) return;
                    var payload = new byte[payloadLength];
                    if (!ReadExactly(connection.Stream, payload)) return;
                    var frame = new LiveLinkFrame { Connection = connection.Id, Type = type, Token = Encoding.UTF8.GetString(tokenBytes), Payload = payload };
                    // Anything not of this session ends the connection: nothing it sent is used.
                    if (!LiveLinkFormat.SameToken(frame.Token, token)) return;
                    if (accept != null && !accept(frame)) return;
                    string key = collapseKey?.Invoke(frame);
                    if (key != null) latest[(connection.Id, key)] = frame;
                    else items.Enqueue(new Item(connection.Id, true, frame));
                    if (closingType.HasValue && type == closingType.Value) return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                // The other application went away.
            }
            finally
            {
                bool known;
                lock (connections) known = connections.Remove(connection.Id);
                try { connection.Client.Close(); }
                catch (Exception) { }
                if (known || !connection.Superseded) items.Enqueue(new Item(connection.Id, false, null));
            }
        }

        private static bool ReadExactly(NetworkStream input, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = input.Read(buffer, read, buffer.Length - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        // Main thread: connections, disconnections and frames in arrival order, then the newest collapsed frames.
        private void Pump()
        {
            while (items.TryDequeue(out var item))
            {
                if (item.Frame == null)
                {
                    // A replaced connection (single client) leaves silently: the new one is already up.
                    ConnectionChanged?.Invoke(item.Connection, item.Up);
                    continue;
                }
                Received?.Invoke(item.Frame);
            }
            foreach (var key in latest.Keys)
                if (latest.TryRemove(key, out var frame))
                    Received?.Invoke(frame);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            EditorApplication.update -= Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            if (closingType.HasValue) Broadcast(closingType.Value);
            Connection[] all;
            lock (connections) { all = connections.Values.ToArray(); connections.Clear(); }
            foreach (var connection in all)
                try { connection.Client.Close(); }
                catch (Exception) { }
            try { listener.Stop(); }
            catch (Exception) { }
        }
    }
}
