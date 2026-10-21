using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    /// token (or that doesn't hold together) closes the connection, and nothing it sent is used. Unity sends frames only on
    /// connections that sent it a frame with the token, so a stranger that connects learns nothing.
    /// <para>
    /// Type 0 is the same for every link: a client that wants Unity to prove it holds the session's token before sending
    /// it (a stranger could listen on Unity's port while Unity reloads its scripts) sends first a type 0 frame without a
    /// token whose payload is a random nonce of 16 to 64 bytes. Unity answers with a type 0 frame without a token whose
    /// payload is <see cref="Proof"/>: HMAC-SHA256, keyed with the token's UTF-8 bytes, of the ASCII text
    /// "Orbiters live link proof", the magic (u32), the port the client connected to (u16) and the nonce.
    /// </para>
    /// </summary>
    public sealed class LiveLinkFormat
    {
        public const int HeaderBytes = 12;
        public const int MaxTokenBytes = 256;
        /// <summary>The frame type of a proof request and of its answer, the same on every link.</summary>
        public const byte ProofType = 0;
        public const int MinNonceBytes = 16;
        public const int MaxNonceBytes = 64;

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
        public bool TryReadHeader(byte[] header, out byte type, out int tokenLength, out int payloadLength) =>
            TryReadAnyHeader(header, out type, out tokenLength, out payloadLength) && type != ProofType && knownType(type);

        // A header of this format, whatever its type (the proof type included).
        internal bool TryReadAnyHeader(byte[] header, out byte type, out int tokenLength, out int payloadLength)
        {
            type = 0;
            tokenLength = payloadLength = 0;
            if (header == null || header.Length < HeaderBytes) return false;
            if (BitConverter.ToUInt32(header, 0) != Magic || header[4] != Version) return false;
            type = header[5];
            tokenLength = BitConverter.ToUInt16(header, 6);
            payloadLength = BitConverter.ToInt32(header, 8);
            return tokenLength <= MaxTokenBytes && payloadLength >= 0 && payloadLength <= MaxPayloadBytes;
        }

        /// <summary>What Unity answers to a proof request with this nonce, on this port (see the class remarks).</summary>
        public byte[] Proof(string token, int port, byte[] nonce)
        {
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            var label = Encoding.ASCII.GetBytes("Orbiters live link proof");
            var message = new byte[label.Length + 4 + 2 + nonce.Length];
            label.CopyTo(message, 0);
            BitConverter.GetBytes(Magic).CopyTo(message, label.Length);
            BitConverter.GetBytes((ushort)port).CopyTo(message, label.Length + 4);
            nonce.CopyTo(message, label.Length + 6);
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token ?? string.Empty))) return hmac.ComputeHash(message);
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
    /// connections and disconnections). A connection counts (it is announced, and frames are sent to it) only once it sent
    /// a frame with the session's token; one that does not within a few seconds is closed. Frames waiting with the same
    /// collapse key (the positions of one mesh, the newest image of one texture) collapse to the newest, in the place of
    /// the first of them: a frame that came after them (a whole mesh, a goodbye) is still handled after them. A connection
    /// whose frames wait unhandled past a cap is read no further until the editor caught up. Sends are queued and written
    /// by a thread per connection, so the editor never waits on a peer that is busy. Closed before script reloads.
    /// </summary>
    public sealed class LiveLinkServer : IDisposable
    {
        private sealed class Connection
        {
            public int Id;
            public TcpClient Client;
            public NetworkStream Stream;
            // What waits to be written, in order, and its size (a peer that stops reading is cut off past a cap).
            public readonly BlockingCollection<byte[]> Outgoing = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
            public long QueuedBytes;
            // Payload bytes read but not handled on the main thread yet: past a cap, the reader waits.
            public long InboundBytes;
            public readonly ManualResetEventSlim Drained = new ManualResetEventSlim(true);
            public Thread Writer;
            // Closes a connection that did not prove it belongs to the session in time; Proved once it sent the token.
            public Timer Deadline;
            public volatile bool Proved;
            // Replaced by a newer connection (single-client links): its departure was announced already.
            public volatile bool Superseded;
            // A connection is announced (and replaces the previous one on single-client links) only once it sent a frame
            // with the session's token: a stranger that connects is told nothing and cannot push the real client off.
            public volatile bool Announced;
            public bool ProofSent;
            // Its reader and writer threads: the last one to end disposes what they shared.
            public int Running = 2;
        }

        // A connection or a disconnection (Frame and Collapsed null), a frame and the payload bytes it was counted for, or the
        // place of collapsed frames.
        private readonly struct Item
        {
            public readonly Connection Owner;
            public readonly bool Up;
            public readonly LiveLinkFrame Frame;
            public readonly int Bytes;
            public readonly (int, string, int)? Collapsed;
            public Item(Connection owner, bool up, LiveLinkFrame frame, int bytes = 0, (int, string, int)? collapsed = null)
            {
                Owner = owner; Up = up; Frame = frame; Bytes = bytes; Collapsed = collapsed;
            }
        }

        /// <summary>Bytes a connection may have waiting to be written before it is closed as not reading.</summary>
        public const long MaxQueuedBytes = 512L * 1024 * 1024;
        /// <summary>
        /// Connections that have not proved they belong to the session yet, at most: one more closes the oldest of them that
        /// has not sent the token (strangers holding every place cannot keep the client out).
        /// </summary>
        public const int MaxPendingConnections = 16;
        /// <summary>How long a new connection has to send a frame with the session's token.</summary>
        internal static int HandshakeMilliseconds = 10000;
        /// <summary>How long a replaced connection's goodbye (after what was queued for it) has to leave before it is closed.</summary>
        internal static int ReplacedDrainMilliseconds = 2000;

        private readonly LiveLinkFormat format;
        private readonly string token;
        private readonly bool singleClient;
        private readonly byte? closingType;
        private readonly byte[] replacedGoodbye;
        private readonly Func<LiveLinkFrame, bool> accept;
        private readonly Func<LiveLinkFrame, string> collapseKey;
        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly string threadName;
        private readonly long maxInboundBytes;
        private readonly Dictionary<int, Connection> connections = new Dictionary<int, Connection>();
        private readonly ConcurrentQueue<Item> items = new ConcurrentQueue<Item>();
        // Newest waiting frame per (connection, collapse key, run), with the payload bytes it was counted for (an accept check
        // may let go of the payload): a run ends when a frame that does not collapse comes.
        private readonly Dictionary<(int, string, int), (LiveLinkFrame Frame, int Bytes)> latest = new Dictionary<(int, string, int), (LiveLinkFrame, int)>();
        private readonly HashSet<Connection> unannounced = new HashSet<Connection>();
        // Main thread: the connections announced and not gone yet (frames of any other are dropped).
        private readonly HashSet<int> live = new HashSet<int>();
        private int nextId;
        private volatile bool disposed;

        /// <summary>A frame reached the main thread (already checked: this session's token, a payload that holds together).</summary>
        public event Action<LiveLinkFrame> Received;
        /// <summary>A connection opened (true: it sent a frame with the session's token) or closed (false), on the main thread.</summary>
        public event Action<int, bool> ConnectionChanged;

        public int Port { get; }
        public bool Connected { get { lock (connections) return connections.Count > 0; } }
        public int[] Connections { get { lock (connections) return connections.Keys.ToArray(); } }

        /// <param name="port">0 for any free port; a port to listen on again after a script reload (any other when taken).</param>
        /// <param name="singleClient">One connection at a time: a new one replaces the previous (an app reconnecting after a crash).</param>
        /// <param name="accept">Checks (and decodes into <see cref="LiveLinkFrame.Decoded"/>) a frame on its connection's thread;
        /// false (or an exception) closes the connection.</param>
        /// <param name="collapseKey">Frames waiting with the same key (per connection) collapse to the newest; null: none do.</param>
        /// <param name="closingType">The frame type that says goodbye: the connection closes after it, and it is sent on dispose.</param>
        /// <param name="replacedGoodbye">On single-client links with a closing type: the payload of the goodbye a connection gets
        /// when a newer one replaces it (null: an empty one), so the app can tell another client taking the link from Unity
        /// going away (where it links again on its own).</param>
        public LiveLinkServer(LiveLinkFormat format, string token, int port = 0, bool singleClient = false, Func<LiveLinkFrame, bool> accept = null,
            Func<LiveLinkFrame, string> collapseKey = null, byte? closingType = null, string threadName = "Orbiters live link", byte[] replacedGoodbye = null)
        {
            this.format = format ?? throw new ArgumentNullException(nameof(format));
            this.token = token ?? throw new ArgumentNullException(nameof(token));
            this.singleClient = singleClient;
            this.accept = accept;
            this.collapseKey = collapseKey;
            this.closingType = closingType;
            this.replacedGoodbye = replacedGoodbye;
            this.threadName = threadName;
            maxInboundBytes = Math.Max(4L * format.MaxPayloadBytes, 64L * 1024 * 1024);
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

        /// <summary>Queues a frame on one connection; false when it is gone (or was cut off for not reading).</summary>
        public bool Send(int connection, byte type, byte[] payload = null)
        {
            Connection target;
            lock (connections) connections.TryGetValue(connection, out target);
            return target != null && Write(target, format.Encode(type, token, payload));
        }

        /// <summary>Queues a frame on every connection; how many took it.</summary>
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
            Close(target);
        }

        private static void Close(Connection connection)
        {
            try { connection?.Client.Close(); }
            catch (Exception) { }
        }

        private static bool Write(Connection connection, byte[] data)
        {
            try
            {
                if (connection.Outgoing.IsAddingCompleted) return false;
                if (Interlocked.Add(ref connection.QueuedBytes, data.Length) > MaxQueuedBytes)
                {
                    // The peer stopped reading long ago: better a reconnect than the editor's memory filling up.
                    Close(connection);
                    return false;
                }
                connection.Outgoing.Add(data);
                return true;
            }
            // Completed, or disposed once its threads ended (ObjectDisposedException is one).
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // No more sends: the writer ends once it wrote what waits. Nothing when the connection is over already.
        private static void Complete(Connection connection)
        {
            try { connection.Outgoing.CompleteAdding(); }
            catch (ObjectDisposedException) { }
        }

        // The reader may read again.
        private static void Wake(Connection connection)
        {
            try { connection.Drained.Set(); }
            catch (ObjectDisposedException) { }
        }

        // A connection's reader or writer ended: the last of them disposes what they shared.
        private static void Finished(Connection connection)
        {
            if (Interlocked.Decrement(ref connection.Running) != 0) return;
            connection.Deadline?.Dispose();
            connection.Outgoing.Dispose();
            connection.Drained.Dispose();
        }

        private static void WriteLoop(Connection connection)
        {
            try
            {
                foreach (var data in connection.Outgoing.GetConsumingEnumerable())
                {
                    connection.Stream.Write(data, 0, data.Length);
                    Interlocked.Add(ref connection.QueuedBytes, -data.Length);
                }
            }
            catch (Exception)
            {
                // The peer went away (or the stream was closed under the writer): the reader sees it too and announces it.
                Close(connection);
            }
            finally
            {
                Finished(connection);
            }
        }

        /// <summary>
        /// Writes a small file (an endpoint the other application reads while it may change), at best whole or not at all.
        /// The file is written beside it under a name of its own, then put in its place; while a reader holds it open
        /// (Windows refuses the replacement then), it is tried again a few times, then overwritten in place, which a reader
        /// may see half done. Never throws: a file that cannot be written is said in the Console.
        /// </summary>
        public static void WriteAtomically(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, text);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(15);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Still held: a reader that sees a half file reads it again later; a stale one would mislead it.
                        File.Copy(temp, path, true);
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Held without sharing writes, or not writable: the caller goes on, the next write may succeed.
                UnityEngine.Debug.LogWarning("[Orbiters] " + Path.GetFileName(path) + " could not be updated: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { }
            }
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                TcpClient accepted;
                try { accepted = listener.AcceptTcpClient(); }
                catch (Exception) when (disposed) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception)
                {
                    // Not one connection's fault (the listener itself): not again at once.
                    Thread.Sleep(100);
                    continue;
                }

                try { Start(accepted); }
                catch (Exception)
                {
                    // That connection went away while it was set up.
                    try { accepted.Close(); }
                    catch (Exception) { }
                }
            }
        }

        private void Start(TcpClient accepted)
        {
            accepted.NoDelay = true;
            var connection = new Connection { Id = Interlocked.Increment(ref nextId), Client = accepted, Stream = accepted.GetStream() };
            Connection evicted = null;
            lock (connections)
            {
                if (!disposed && unannounced.Count >= MaxPendingConnections)
                {
                    // The oldest that has not sent the token makes room: a client connecting now proves itself at once.
                    evicted = unannounced.Where(c => !c.Proved).OrderBy(c => c.Id).FirstOrDefault();
                    if (evicted != null) unannounced.Remove(evicted);
                }
                if (disposed || unannounced.Count >= MaxPendingConnections)
                {
                    accepted.Close();
                    return;
                }
                unannounced.Add(connection);
            }
            Close(evicted);
            connection.Deadline = new Timer(_ => { if (!connection.Proved) Close(connection); }, null, HandshakeMilliseconds, Timeout.Infinite);
            connection.Writer = new Thread(() => WriteLoop(connection)) { IsBackground = true, Name = threadName + " writer" };
            connection.Writer.Start();
            var reader = new Thread(() => ReadLoop(connection)) { IsBackground = true, Name = threadName + " reader" };
            reader.Start();
        }

        private void ReadLoop(Connection connection)
        {
            var header = new byte[LiveLinkFormat.HeaderBytes];
            int run = 0;
            try
            {
                while (!disposed && !connection.Superseded && connection.Client.Connected)
                {
                    // The editor has not caught up with this connection's frames: wait (the peer's writes wait in turn).
                    while (Interlocked.Read(ref connection.InboundBytes) > maxInboundBytes)
                    {
                        if (disposed || connection.Superseded) return;
                        connection.Drained.Reset();
                        if (Interlocked.Read(ref connection.InboundBytes) > maxInboundBytes) connection.Drained.Wait(100);
                    }
                    if (!ReadExactly(connection.Stream, header)) return;
                    if (!format.TryReadAnyHeader(header, out byte type, out int tokenLength, out int payloadLength)) return;
                    if (type == LiveLinkFormat.ProofType)
                    {
                        if (!AnswerProof(connection, tokenLength, payloadLength)) return;
                        continue;
                    }
                    if (!format.TryReadHeader(header, out _, out _, out _)) return;
                    var tokenBytes = new byte[tokenLength];
                    if (!ReadExactly(connection.Stream, tokenBytes)) return;
                    string frameToken = Encoding.UTF8.GetString(tokenBytes);
                    // Anything not of this session ends the connection: nothing it sent is used, nor its payload read.
                    if (!LiveLinkFormat.SameToken(frameToken, token)) return;
                    // It holds the token: no deadline to read the rest of its first frame in.
                    connection.Proved = true;
                    connection.Deadline?.Dispose();
                    var payload = new byte[payloadLength];
                    if (!ReadExactly(connection.Stream, payload)) return;
                    var frame = new LiveLinkFrame { Connection = connection.Id, Type = type, Token = frameToken, Payload = payload };
                    if (accept != null && !accept(frame)) return;
                    if (connection.Superseded) return;
                    if (!connection.Announced) Announce(connection);
                    string key = collapseKey?.Invoke(frame);
                    // Counted as read: what the frame holds by then may differ (an accept check that lets go of the payload).
                    if (key == null)
                    {
                        run++;
                        Interlocked.Add(ref connection.InboundBytes, payload.Length);
                        items.Enqueue(new Item(connection, true, frame, payload.Length));
                    }
                    else
                    {
                        // The first frame of a run takes a place in the order; later ones only replace what it delivers.
                        var slot = (connection.Id, key, run);
                        bool added;
                        lock (latest)
                        {
                            added = !latest.TryGetValue(slot, out var older);
                            Interlocked.Add(ref connection.InboundBytes, payload.Length - (added ? 0 : older.Bytes));
                            latest[slot] = (frame, payload.Length);
                        }
                        if (added) items.Enqueue(new Item(connection, true, null, collapsed: slot));
                    }
                    if (closingType.HasValue && type == closingType.Value) return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                // The other application went away (or did not prove it belongs to the session in time).
            }
            catch (Exception ex)
            {
                // The link's own check of a frame failed: that connection ends, the editor goes on.
                UnityEngine.Debug.LogWarning("[" + threadName + "] A frame could not be read, so its connection was closed: " + ex);
            }
            finally
            {
                lock (connections) { connections.Remove(connection.Id); unannounced.Remove(connection); }
                connection.Deadline?.Dispose();
                Complete(connection);
                Close(connection);
                if (connection.Announced && !connection.Superseded) items.Enqueue(new Item(connection, false, null));
                Finished(connection);
            }
        }

        // A proof request (before the connection sent the token, once): the nonce is read and the proof queued.
        private bool AnswerProof(Connection connection, int tokenLength, int nonceLength)
        {
            if (connection.Announced || connection.ProofSent || tokenLength != 0) return false;
            if (nonceLength < LiveLinkFormat.MinNonceBytes || nonceLength > LiveLinkFormat.MaxNonceBytes) return false;
            var nonce = new byte[nonceLength];
            if (!ReadExactly(connection.Stream, nonce)) return false;
            connection.ProofSent = true;
            return Write(connection, format.Encode(LiveLinkFormat.ProofType, null, format.Proof(token, Port, nonce)));
        }

        // A connection proved it belongs to the session: it is announced. On single-client links it replaces the previous
        // one, whose departure is announced first, so the app can let go of what that one held. With a closing type, the
        // replaced one gets its goodbye after what was queued for it, then is closed (at the latest after a short while).
        private void Announce(Connection connection)
        {
            Connection[] replaced = Array.Empty<Connection>();
            lock (connections)
            {
                unannounced.Remove(connection);
                if (singleClient)
                {
                    replaced = connections.Values.ToArray();
                    foreach (var old in replaced) { old.Superseded = true; connections.Remove(old.Id); }
                }
                connections[connection.Id] = connection;
                connection.Announced = true;
            }
            connection.Deadline?.Dispose();
            foreach (var old in replaced)
            {
                items.Enqueue(new Item(old, false, null));
                if (closingType.HasValue) Write(old, format.Encode(closingType.Value, token, replacedGoodbye));
                Complete(old);
                Wake(old);
                if (!closingType.HasValue) { Close(old); continue; }
                var writer = old.Writer;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { writer?.Join(ReplacedDrainMilliseconds); }
                    catch (Exception) { }
                    Close(old);
                });
            }
            items.Enqueue(new Item(connection, true, null));
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

        // Main thread: connections, disconnections and frames in arrival order; collapsed frames in the place of the
        // first of their run, as the newest of it. Frames of a connection that is gone (replaced, closed) are dropped.
        private void Pump()
        {
            while (!disposed && items.TryDequeue(out var item))
            {
                var owner = item.Owner;
                LiveLinkFrame frame = item.Frame;
                int bytes = item.Bytes;
                if (item.Collapsed.HasValue)
                {
                    lock (latest)
                    {
                        if (latest.TryGetValue(item.Collapsed.Value, out var newest))
                        {
                            latest.Remove(item.Collapsed.Value);
                            (frame, bytes) = newest;
                        }
                    }
                    if (frame == null) continue;
                }
                else if (frame == null)
                {
                    if (item.Up) live.Add(owner.Id);
                    else live.Remove(owner.Id);
                    ConnectionChanged?.Invoke(owner.Id, item.Up);
                    continue;
                }
                if (Interlocked.Add(ref owner.InboundBytes, -bytes) <= maxInboundBytes) Wake(owner);
                if (live.Contains(owner.Id)) Received?.Invoke(frame);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            EditorApplication.update -= Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            // Only to connections of the session: a stranger never hears from Unity.
            if (closingType.HasValue) Broadcast(closingType.Value);
            Connection[] all;
            lock (connections) { all = connections.Values.Concat(unannounced).ToArray(); connections.Clear(); unannounced.Clear(); }
            // The goodbye gets a moment to leave; a peer that is not reading does not hold the reload up.
            foreach (var connection in all) { Complete(connection); Wake(connection); connection.Deadline?.Dispose(); }
            var until = DateTime.UtcNow.AddMilliseconds(250);
            foreach (var connection in all)
            {
                int left = (int)Math.Max(0, (until - DateTime.UtcNow).TotalMilliseconds);
                connection.Writer?.Join(left);
            }
            foreach (var connection in all) Close(connection);
            try { listener.Stop(); }
            catch (Exception) { }
        }
    }
}
