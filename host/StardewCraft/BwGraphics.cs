using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace StardewCraft
{
    /// <summary>Decodes StarCraft GRP frames from the user's extracted data into textures, on demand.</summary>
    internal sealed class BwGraphics : IDisposable
    {
        private const int ImageCount = 999;
        private const byte ModifierShadow = 10;

        // Base colors for BW player colors 0-7; indices 8-15 of unit art are shaded from these.
        private static readonly Color[] PlayerColors =
        {
            new(244, 4, 4), new(12, 72, 204), new(44, 180, 148), new(136, 64, 156),
            new(248, 140, 20), new(112, 48, 20), new(204, 224, 208), new(252, 252, 56),
        };

        private readonly string dataDir;
        private readonly GraphicsDevice device;
        private readonly uint[] imageGrp = new uint[ImageCount];
        private readonly string[] grpNames;
        private readonly Color[] palette = new Color[256];
        private readonly Dictionary<int, Grp?> grps = new();
        private readonly Dictionary<(int image, int frame, int color, bool shadow), Texture2D?> textures = new();

        public BwGraphics(string dataDir, string tileset, GraphicsDevice device)
        {
            this.dataDir = dataDir;
            this.device = device;

            // images.dat starts with the u32 GRP index (1-based into images.tbl) of each image.
            byte[] imagesDat = File.ReadAllBytes(Path.Combine(dataDir, "arr", "images.dat"));
            for (int i = 0; i < ImageCount; i++)
                this.imageGrp[i] = BitConverter.ToUInt32(imagesDat, i * 4);
            this.grpNames = ReadTbl(Path.Combine(dataDir, "arr", "images.tbl"));

            byte[] wpe = File.ReadAllBytes(Path.Combine(dataDir, "TileSet", tileset + ".wpe"));
            for (int i = 0; i < 256; i++)
                this.palette[i] = new Color(wpe[i * 4], wpe[i * 4 + 1], wpe[i * 4 + 2]);
        }

        /// <summary>Returns the texture for one frame, or null if it cannot be drawn.</summary>
        public Texture2D? GetFrame(ImageEntry e)
        {
            bool shadow = e.Modifier == ModifierShadow;
            var key = (e.ImageId, (int)e.Frame, shadow ? 0 : (int)e.Color, shadow);
            if (this.textures.TryGetValue(key, out Texture2D? cached))
                return cached;

            Texture2D? tex = null;
            Grp? grp = this.GetGrp(e.ImageId);
            if (grp != null && e.Frame < grp.FrameCount)
                tex = this.Decode(grp, e.Frame, e.Color, shadow);
            this.textures[key] = tex;
            return tex;
        }

        private Grp? GetGrp(int imageId)
        {
            if (this.grps.TryGetValue(imageId, out Grp? grp))
                return grp;

            grp = null;
            if (imageId < ImageCount)
            {
                uint tblIndex = this.imageGrp[imageId];
                if (tblIndex > 0 && tblIndex <= this.grpNames.Length)
                {
                    string path = Path.Combine(this.dataDir, "unit", this.grpNames[tblIndex - 1].Replace('\\', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                        grp = new Grp(File.ReadAllBytes(path));
                }
            }
            this.grps[imageId] = grp;
            return grp;
        }

        private Texture2D? Decode(Grp grp, int frameIndex, int colorIndex, bool shadow)
        {
            var frame = grp.Frame(frameIndex);
            if (frame.Width == 0 || frame.Height == 0)
                return null;

            Color team = PlayerColors[colorIndex % PlayerColors.Length];
            var pixels = new Color[frame.Width * frame.Height];
            byte[] d = grp.Data;
            for (int y = 0; y < frame.Height; y++)
            {
                int p = frame.Offset + BitConverter.ToUInt16(d, frame.Offset + y * 2);
                int x = 0;
                while (x < frame.Width && p < d.Length)
                {
                    byte b = d[p++];
                    if ((b & 0x80) != 0)
                    {
                        x += b & 0x7f;
                    }
                    else if ((b & 0x40) != 0)
                    {
                        int n = b & 0x3f;
                        byte v = d[p++];
                        for (int i = 0; i < n && x < frame.Width; i++)
                            pixels[y * frame.Width + x++] = this.Shade(v, team, shadow);
                    }
                    else
                    {
                        for (int i = 0; i < b && x < frame.Width; i++)
                            pixels[y * frame.Width + x++] = this.Shade(d[p++], team, shadow);
                    }
                }
            }

            var tex = new Texture2D(this.device, frame.Width, frame.Height);
            tex.SetData(pixels);
            return tex;
        }

        private Color Shade(byte index, Color team, bool shadow)
        {
            if (shadow)
                return new Color(0, 0, 0, 110);
            if (index >= 8 && index < 16)
            {
                float f = 1f - (index - 8) * 0.1f;
                return new Color((int)(team.R * f), (int)(team.G * f), (int)(team.B * f));
            }
            return this.palette[index];
        }

        private static string[] ReadTbl(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            int count = BitConverter.ToUInt16(d, 0);
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                int start = BitConverter.ToUInt16(d, 2 + i * 2);
                int end = start;
                while (end < d.Length && d[end] != 0)
                    end++;
                result[i] = Encoding.ASCII.GetString(d, start, end - start);
            }
            return result;
        }

        public void Dispose()
        {
            foreach (Texture2D? tex in this.textures.Values)
                tex?.Dispose();
            this.textures.Clear();
        }

        private sealed class Grp
        {
            public readonly byte[] Data;
            public readonly int FrameCount;

            public Grp(byte[] data)
            {
                this.Data = data;
                this.FrameCount = BitConverter.ToUInt16(data, 0);
            }

            public (int Width, int Height, int Offset) Frame(int i)
            {
                int h = 6 + i * 8;
                return (this.Data[h + 2], this.Data[h + 3], (int)BitConverter.ToUInt32(this.Data, h + 4));
            }
        }
    }
}
