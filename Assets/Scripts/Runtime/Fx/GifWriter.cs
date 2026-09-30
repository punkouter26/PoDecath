using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PoDecath.Fx
{
    /// <summary>
    /// A small animated-GIF encoder: GIF89a, one global 256-colour palette, LZW, looping.
    ///
    /// Why a GIF and why by hand: the highlight clip has to be something a phone can save and a person can
    /// post anywhere, on every platform this game ships to, and every free runtime video encoder for Unity
    /// is desktop-only (FFmpegOut shells out to an ffmpeg binary) while the mobile ones are paid. A GIF is
    /// the one moving-picture format every messaging app, browser and gallery plays, and its encoder is two
    /// hundred lines of well-known code with no native library. The cost is colour: 252 fixed colours with
    /// ordered dithering, which on a sunny roof with a coloured field reads fine at clip size.
    ///
    /// Thread-safe by construction: <see cref="Encode"/> touches no Unity API, so it runs on a worker thread
    /// while the game carries on.
    /// </summary>
    public static class GifWriter
    {
        // 6 levels of red, 7 of green (the eye's most sensitive channel gets the extra step), 6 of blue.
        const int R = 6, G = 7, B = 6;

        static readonly Color32[] _palette = BuildPalette();
        public static Color32[] Palette => _palette;

        static Color32[] BuildPalette()
        {
            var p = new Color32[256];
            for (int r = 0; r < R; r++)
                for (int g = 0; g < G; g++)
                    for (int b = 0; b < B; b++)
                        p[r * G * B + g * B + b] = new Color32((byte)(r * 255 / (R - 1)), (byte)(g * 255 / (G - 1)), (byte)(b * 255 / (B - 1)), 255);
            for (int i = R * G * B; i < 256; i++) p[i] = new Color32(0, 0, 0, 255);
            return p;
        }

        // 4x4 Bayer matrix, centred on zero. Ordered rather than error-diffusion dithering on purpose: it is
        // stable from frame to frame, so a still background does not crawl in the animation.
        static readonly float[] _bayer =
        {
             0f,  8f,  2f, 10f,
            12f,  4f, 14f,  6f,
             3f, 11f,  1f,  9f,
            15f,  7f, 13f,  5f,
        };

        /// <summary>
        /// Quantises one RGBA32 frame into palette indices. <paramref name="bottomUp"/> is how a GPU readback
        /// arrives (row 0 is the bottom of the picture); GIF wants the top row first, so it is flipped here.
        /// </summary>
        public static void Quantise(Unity.Collections.NativeArray<byte> rgba, int w, int h, byte[] into, bool bottomUp)
        {
            float stepR = 255f / (R - 1), stepG = 255f / (G - 1), stepB = 255f / (B - 1);
            for (int y = 0; y < h; y++)
            {
                int src = (bottomUp ? h - 1 - y : y) * w * 4;
                int dst = y * w;
                for (int x = 0; x < w; x++, src += 4)
                {
                    float d = (_bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
                    int r = Mathf.Clamp(Mathf.RoundToInt(rgba[src] / stepR + d), 0, R - 1);
                    int g = Mathf.Clamp(Mathf.RoundToInt(rgba[src + 1] / stepG + d), 0, G - 1);
                    int b = Mathf.Clamp(Mathf.RoundToInt(rgba[src + 2] / stepB + d), 0, B - 1);
                    into[dst + x] = (byte)(r * G * B + g * B + b);
                }
            }
        }

        /// <summary>Writes the frames (palette indices, top row first) as a looping GIF.</summary>
        public static void Encode(Stream s, int w, int h, IList<byte[]> frames, int delayCentiseconds)
        {
            var bw = new BinaryWriter(s);
            bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bw.Write((ushort)w);
            bw.Write((ushort)h);
            bw.Write((byte)0xF7);          // global colour table, 8 bits per channel, 256 entries
            bw.Write((byte)0);             // background colour index
            bw.Write((byte)0);             // pixel aspect ratio: unspecified (square)
            foreach (Color32 c in _palette) { bw.Write(c.r); bw.Write(c.g); bw.Write(c.b); }

            // NETSCAPE2.0: loop for ever. Without it most viewers play the clip once and stop on the last frame.
            bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)11);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write((byte)3); bw.Write((byte)1); bw.Write((ushort)0); bw.Write((byte)0);

            var lzw = new Lzw();
            foreach (byte[] frame in frames)
            {
                // Graphic control: the delay, no transparency, no disposal needed for full frames.
                bw.Write((byte)0x21); bw.Write((byte)0xF9); bw.Write((byte)4);
                bw.Write((byte)0x04);
                bw.Write((ushort)Mathf.Max(2, delayCentiseconds));
                bw.Write((byte)0); bw.Write((byte)0);

                // Image descriptor: the whole canvas, global palette, not interlaced.
                bw.Write((byte)0x2C);
                bw.Write((ushort)0); bw.Write((ushort)0);
                bw.Write((ushort)w); bw.Write((ushort)h);
                bw.Write((byte)0);

                lzw.Write(bw, frame, w * h);
            }
            bw.Write((byte)0x3B);          // trailer
            bw.Flush();
        }

        /// <summary>GIF's variable-width LZW with 8-bit roots, packed into 255-byte sub-blocks.</summary>
        sealed class Lzw
        {
            const int MinCodeSize = 8, MaxCode = 4095;
            readonly Dictionary<int, int> _table = new Dictionary<int, int>(8192);
            readonly byte[] _block = new byte[255];
            int _blockLen, _bitBuf, _bitCount, _codeSize, _next;
            BinaryWriter _w;

            public void Write(BinaryWriter w, byte[] pixels, int count)
            {
                _w = w;
                _blockLen = 0; _bitBuf = 0; _bitCount = 0;
                int clear = 1 << MinCodeSize, end = clear + 1;
                w.Write((byte)MinCodeSize);

                Reset(clear);
                Emit(clear);
                int prefix = pixels[0];
                for (int i = 1; i < count; i++)
                {
                    int k = pixels[i];
                    int key = (prefix << 8) | k;
                    if (_table.TryGetValue(key, out int code)) { prefix = code; continue; }
                    Emit(prefix);
                    if (_next <= MaxCode)
                    {
                        _table[key] = _next++;
                        // The width grows once the code just assigned no longer fits, which is exactly when
                        // a decoder, one step behind the encoder, will make the same change.
                        if (_next > (1 << _codeSize) && _codeSize < 12) _codeSize++;
                    }
                    else
                    {
                        Emit(clear);
                        Reset(clear);
                    }
                    prefix = k;
                }
                Emit(prefix);
                Emit(end);
                if (_bitCount > 0) Byte((byte)(_bitBuf & 0xFF));
                FlushBlock();
                w.Write((byte)0);          // block terminator
            }

            void Reset(int clear)
            {
                _table.Clear();
                _codeSize = MinCodeSize + 1;
                _next = clear + 2;
            }

            void Emit(int code)
            {
                _bitBuf |= code << _bitCount;
                _bitCount += _codeSize;
                while (_bitCount >= 8)
                {
                    Byte((byte)(_bitBuf & 0xFF));
                    _bitBuf >>= 8;
                    _bitCount -= 8;
                }
            }

            void Byte(byte b)
            {
                _block[_blockLen++] = b;
                if (_blockLen == 255) FlushBlock();
            }

            void FlushBlock()
            {
                if (_blockLen == 0) return;
                _w.Write((byte)_blockLen);
                _w.Write(_block, 0, _blockLen);
                _blockLen = 0;
            }
        }
    }
}
