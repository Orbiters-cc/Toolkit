using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Orbiters.Toolkit.Editor.LiveLink
{
    /// <summary>JSON payloads of live-link frames, and the endpoint files a client reads to find the editor.</summary>
    public static class LiveLinkJson
    {
        public static byte[] Encode(object value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));

        public static string Text(object value) => JsonConvert.SerializeObject(value, Formatting.Indented);

        public static T Decode<T>(byte[] payload) =>
            JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(payload ?? Array.Empty<byte>()));

        public static T Parse<T>(string text) => JsonConvert.DeserializeObject<T>(text);
    }

    /// <summary>
    /// The file an external application reads to find a live-link server: written atomically (a client never sees
    /// a half file) and deleted when the server goes. One file per editor process, so several editors coexist.
    /// </summary>
    public static class LiveLinkEndpointFile
    {
        /// <summary>Writes the endpoint description (any serializable object) at <paramref name="path"/>.</summary>
        public static void Write(string path, object endpoint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            LiveLinkServer.WriteAtomically(path, LiveLinkJson.Text(endpoint));
        }

        public static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? LiveLinkJson.Parse<T>(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
