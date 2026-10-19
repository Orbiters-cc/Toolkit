using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.LiveLink;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.Textures;
using UnityEngine;
using UnityEngine.TestTools;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class LiveLinkTests
    {
        private static readonly LiveLinkFormat Format = new LiveLinkFormat(0x54534554, 1, 1 << 20, type => type >= 1 && type <= 4);

        private static IEnumerator Until(Func<bool> done, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < deadline) yield return null;
        }

        private static TcpClient Connect(LiveLinkServer server) => new TcpClient("127.0.0.1", server.Port) { NoDelay = true };

        private static void Send(TcpClient client, byte type, string token, string text)
        {
            var data = Format.Encode(type, token, Encoding.UTF8.GetBytes(text));
            client.GetStream().Write(data, 0, data.Length);
        }

        [Test]
        public void FramesRoundTripAndForeignHeadersAreRefused()
        {
            var data = Format.Encode(2, "tok", new byte[] { 1, 2, 3 });
            Assert.That(Format.TryReadHeader(data, out byte type, out int tokenLength, out int payloadLength), Is.True);
            Assert.That((type, tokenLength, payloadLength), Is.EqualTo(((byte)2, 3, 3)));
            var unknown = Format.Encode(2, "tok", null);
            unknown[5] = 9;
            Assert.That(Format.TryReadHeader(unknown, out _, out _, out _), Is.False, "A frame type the link doesn't know.");
            var other = new LiveLinkFormat(0x11111111, 1, 1 << 20, null).Encode(2, "tok", null);
            Assert.That(Format.TryReadHeader(other, out _, out _, out _), Is.False, "Another link's frames.");
            Assert.That(LiveLinkFormat.SameToken("abc", "abc"), Is.True);
            Assert.That(LiveLinkFormat.SameToken("abc", "abd"), Is.False);
        }

        // Several applications at once, each frame on the main thread with its connection; one with another token is cut off
        // without anything it sent being used.
        [UnityTest]
        public IEnumerator ConnectionsAreKeptApartAndStrangersAreCutOff()
        {
            var frames = new List<LiveLinkFrame>();
            var changes = new List<(int, bool)>();
            using (var server = new LiveLinkServer(Format, "secret"))
            {
                server.Received += frames.Add;
                server.ConnectionChanged += (id, up) => changes.Add((id, up));
                using (var first = Connect(server))
                using (var second = Connect(server))
                {
                    yield return Until(() => changes.Count >= 2);
                    Send(first, 1, "secret", "one");
                    Send(second, 1, "secret", "two");
                    yield return Until(() => frames.Count >= 2);
                    Assert.That(frames.Select(f => Encoding.UTF8.GetString(f.Payload)), Is.EquivalentTo(new[] { "one", "two" }));
                    Assert.That(frames.Select(f => f.Connection).Distinct().Count(), Is.EqualTo(2));
                    Assert.That(server.Connections.Length, Is.EqualTo(2));

                    Send(second, 1, "wrong", "intruder");
                    yield return Until(() => changes.Any(c => !c.Item2));
                    Assert.That(frames.Any(f => Encoding.UTF8.GetString(f.Payload) == "intruder"), Is.False);
                    Assert.That(server.Connections.Length, Is.EqualTo(1));

                    int reached = server.Broadcast(3, Encoding.UTF8.GetBytes("hello"));
                    Assert.That(reached, Is.EqualTo(1));
                }
            }
        }

        // Frames sharing a collapse key keep only the newest waiting one (images of one texture rendered faster than shown).
        [UnityTest]
        public IEnumerator WaitingFramesCollapseToTheNewest()
        {
            var frames = new List<string>();
            bool up = false;
            using (var server = new LiveLinkServer(Format, "secret", collapseKey: f => f.Type == 2 ? "image" : null))
            {
                server.ConnectionChanged += (_, connected) => up = connected;
                server.Received += f => frames.Add(Encoding.UTF8.GetString(f.Payload));
                using (var client = Connect(server))
                {
                    yield return Until(() => up);
                    // Sent within one editor frame: the pump sees all three at once.
                    var stream = client.GetStream();
                    foreach (var text in new[] { "old", "older", "newest" })
                    {
                        var data = Format.Encode(2, "secret", Encoding.UTF8.GetBytes(text));
                        stream.Write(data, 0, data.Length);
                    }
                    var end = Format.Encode(1, "secret", Encoding.UTF8.GetBytes("end"));
                    stream.Write(end, 0, end.Length);
                    yield return Until(() => frames.Contains("end"));
                    yield return null;
                }
            }
            Assert.That(frames.Last(f => f != "end"), Is.EqualTo("newest"), "The newest image is the one shown last.");
            Assert.That(frames.Count(f => f != "end"), Is.InRange(1, 3), "Collapsing only drops older ones.");
        }

        // One application at a time: a reconnection that proves it belongs to the session (its first frame carries the
        // token) replaces the previous connection, whose departure is announced first; a stranger replaces nothing.
        [UnityTest]
        public IEnumerator SingleClientLinksReplaceTheOldConnectionOnlyForTheSession()
        {
            var changes = new List<(int, bool)>();
            using (var server = new LiveLinkServer(Format, "secret", singleClient: true))
            {
                server.ConnectionChanged += (id, up) => changes.Add((id, up));
                using (var first = Connect(server))
                {
                    yield return Until(() => false, .2);
                    Assert.That(changes, Is.Empty, "a connection is announced once it sent a frame of the session");
                    Send(first, 1, "secret", "hello");
                    yield return Until(() => changes.Count >= 1);
                    using (var stranger = Connect(server))
                    {
                        Send(stranger, 1, "wrong", "hello");
                        yield return Until(() => false, .3);
                        Assert.That(changes.Count, Is.EqualTo(1), "a stranger neither replaces the client nor is announced");
                        Assert.That(server.Connections, Is.EqualTo(new[] { changes[0].Item1 }));
                    }
                    using (var second = Connect(server))
                    {
                        Send(second, 1, "secret", "hello");
                        yield return Until(() => changes.Count >= 3);
                        yield return Until(() => false, .3);
                        Assert.That(changes.Select(c => c.Item2), Is.EqualTo(new[] { true, false, true }), "the replaced connection leaves, then the new one comes");
                        Assert.That(changes[1].Item1, Is.EqualTo(changes[0].Item1));
                        Assert.That(server.Connections, Is.EqualTo(new[] { changes[2].Item1 }));
                    }
                }
            }
        }

        // A frame that does not collapse, sent after collapsing ones, is still handled after them.
        [UnityTest]
        public IEnumerator CollapsedFramesKeepTheirPlaceInTheOrder()
        {
            var frames = new List<string>();
            bool up = false;
            using (var server = new LiveLinkServer(Format, "secret", collapseKey: f => f.Type == 2 ? "positions" : null))
            {
                server.ConnectionChanged += (_, connected) => up = connected;
                server.Received += f => frames.Add(Encoding.UTF8.GetString(f.Payload));
                using (var client = Connect(server))
                {
                    yield return Until(() => up);
                    var stream = client.GetStream();
                    foreach (var (type, text) in new[] { (2, "p0"), (2, "p1"), (1, "mesh"), (2, "p2"), (1, "end") })
                    {
                        var data = Format.Encode((byte)type, "secret", Encoding.UTF8.GetBytes(text));
                        stream.Write(data, 0, data.Length);
                    }
                    yield return Until(() => frames.Contains("end"));
                }
            }
            int mesh = frames.IndexOf("mesh");
            Assert.That(frames.Take(mesh).All(f => f == "p0" || f == "p1"), Is.True, "positions sent before the mesh come before it: " + string.Join(",", frames));
            Assert.That(frames.Skip(mesh + 1).First(), Is.EqualTo("p2"), "positions sent after it come after it");
        }

        [Test]
        public void TgaImagesReadAsUnityRowsWhateverTheirOrigin()
        {
            // 2×2 true colour, rows from the top: bottom row in Unity's order is the file's second row.
            byte[] Header(byte type, byte depth, byte descriptor) => new byte[] { 0, 0, type, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, depth, descriptor };
            var top = Header(2, 24, 0x20).Concat(new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255 }).ToArray();
            var image = TgaImage.Read(top);
            Assert.That((image.Width, image.Height), Is.EqualTo((2, 2)));
            Assert.That(image.Rgba.Take(8), Is.EqualTo(new byte[] { 0, 0, 255, 255, 255, 255, 255, 255 }), "Unity's first row is the file's last.");
            Assert.That(image.Rgba.Skip(8).Take(4), Is.EqualTo(new byte[] { 255, 0, 0, 255 }), "BGR becomes RGB.");

            // Run-length encoded gray, rows from the bottom: one run of three, then one raw value.
            var gray = Header(11, 8, 0).Concat(new byte[] { 0x82, 40, 0x00, 200 }).ToArray();
            var grayImage = TgaImage.Read(gray);
            Assert.That(grayImage.Gray, Is.True);
            Assert.That(grayImage.Rgba.Where((_, i) => i % 4 == 0), Is.EqualTo(new byte[] { 40, 40, 40, 200 }));

            Assert.Throws<InvalidDataException>(() => TgaImage.Read(Header(2, 24, 0)), "A truncated image.");
            Assert.Throws<InvalidDataException>(() => TgaImage.Read(Header(1, 8, 0).Concat(new byte[16]).ToArray()), "Colour-mapped images aren't read.");
        }

        // What the OBJ writer writes is read back where Unity would put it: X mirrored once each way, faces still outward.
        [Test]
        public void ObjFilesReadBackWhereUnityHadTheMesh()
        {
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(.1f, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back },
                triangles = new[] { 0, 2, 1 },
            };
            string folder = Path.Combine(Path.GetTempPath(), "OrbitersObjTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new ObjWriter();
                writer.Add("Triangle", mesh, Matrix4x4.Translate(new Vector3(0, 2, 0)), new[] { "Skin & Fur.001" });
                string path = Path.Combine(folder, "test.obj");
                writer.Save(path);
                Assert.That(writer.Materials, Is.EqualTo(new[] { "Skin___Fur_001" }));
                Assert.That(File.ReadAllText(Path.ChangeExtension(path, ".mtl")), Does.Contain("newmtl Skin___Fur_001"));
                var read = MeshComparison.Parse(path, File.ReadAllBytes(path)).Single();
                Assert.That(read.Points, Is.EqualTo(new[] { new Vector3(.1f, 2, 0), new Vector3(1, 2, 0), new Vector3(0, 3, 0) }));
                var normal = Vector3.Cross(read.Points[read.Triangles[1]] - read.Points[read.Triangles[0]], read.Points[read.Triangles[2]] - read.Points[read.Triangles[0]]);
                Assert.That(normal.z, Is.LessThan(0), "The face still points where the mesh's normals do (Unity's winding).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
