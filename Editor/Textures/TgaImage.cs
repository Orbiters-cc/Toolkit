using System;
using System.IO;

namespace Orbiters.Toolkit.Editor.Textures
{
    /// <summary>
    /// A Truevision TGA image read into RGBA32 pixels, rows from the bottom (Unity's order for
    /// <see cref="UnityEngine.Texture2D.LoadRawTextureData(byte[])"/>). Reads uncompressed and run-length encoded true colour
    /// (24 and 32 bits) and grayscale (8 bits) images, what painting applications write. Safe on any thread: an image another
    /// application writes is decoded off the editor's thread.
    /// </summary>
    public sealed class TgaImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Width × height × 4 bytes, R G B A.</summary>
        public byte[] Rgba { get; private set; }
        /// <summary>A grayscale image (one value per pixel, copied to R, G and B).</summary>
        public bool Gray { get; private set; }

        public static TgaImage Read(string path) => Read(File.ReadAllBytes(path));

        public static TgaImage Read(byte[] data)
        {
            if (data == null || data.Length < 18) throw new InvalidDataException("Not a TGA image: too short.");
            int idLength = data[0], colorMapType = data[1], imageType = data[2];
            int colorMapLength = data[5] | data[6] << 8, colorMapDepth = data[7];
            int width = data[12] | data[13] << 8, height = data[14] | data[15] << 8, depth = data[16], descriptor = data[17];
            bool rle = imageType == 10 || imageType == 11, gray = imageType == 3 || imageType == 11;
            if (colorMapType != 0 || !(imageType == 2 || imageType == 3 || rle)) throw new InvalidDataException("Unsupported TGA image (type " + imageType + ").");
            if (gray ? depth != 8 : depth != 24 && depth != 32) throw new InvalidDataException("Unsupported TGA pixel depth (" + depth + " bits).");
            if (width <= 0 || height <= 0 || (long)width * height > 16384L * 16384L) throw new InvalidDataException("Unsupported TGA size " + width + "×" + height + ".");
            int bytes = depth / 8;
            int offset = 18 + idLength + (colorMapType != 0 ? colorMapLength * ((colorMapDepth + 7) / 8) : 0);
            int count = width * height;
            var pixels = new byte[count * 4];
            // Each source pixel (BGR(A) or gray) into RGBA.
            void Put(int index, int at)
            {
                int o = index * 4;
                if (gray) { byte v = data[at]; pixels[o] = v; pixels[o + 1] = v; pixels[o + 2] = v; pixels[o + 3] = 255; return; }
                pixels[o] = data[at + 2]; pixels[o + 1] = data[at + 1]; pixels[o + 2] = data[at];
                pixels[o + 3] = bytes == 4 ? data[at + 3] : (byte)255;
            }
            if (!rle)
            {
                if (offset + (long)count * bytes > data.Length) throw new InvalidDataException("Truncated TGA image.");
                for (int i = 0; i < count; i++) Put(i, offset + i * bytes);
            }
            else
            {
                int i = 0, at = offset;
                while (i < count)
                {
                    if (at >= data.Length) throw new InvalidDataException("Truncated TGA image.");
                    int packet = data[at++];
                    int run = (packet & 0x7F) + 1;
                    if (i + run > count) throw new InvalidDataException("Corrupt TGA image.");
                    if ((packet & 0x80) != 0)
                    {
                        if (at + bytes > data.Length) throw new InvalidDataException("Truncated TGA image.");
                        for (int k = 0; k < run; k++) Put(i++, at);
                        at += bytes;
                    }
                    else
                    {
                        if (at + run * bytes > data.Length) throw new InvalidDataException("Truncated TGA image.");
                        for (int k = 0; k < run; k++) { Put(i++, at); at += bytes; }
                    }
                }
            }
            // Rows from the top (descriptor bit 5) are turned to Unity's bottom-up order; right-to-left images (bit 4) are not
            // written by painting applications.
            if ((descriptor & 0x20) != 0)
            {
                int stride = width * 4;
                var row = new byte[stride];
                for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
                {
                    Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
                    Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
                    Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
                }
            }
            return new TgaImage { Width = width, Height = height, Rgba = pixels, Gray = gray };
        }
    }
}
