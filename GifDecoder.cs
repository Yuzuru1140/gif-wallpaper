// Decoder GIF sendiri: parse sekali, simpan data LZW terkompresi per frame (≈ ukuran file),
// lalu decode frame berurutan ke kanvas 32-bit. Jauh lebih cepat dari GDI+ SelectActiveFrame,
// dan aman dipanggil dari thread latar (tidak menyentuh GDI+).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GifWallpaper
{
    unsafe sealed class GifDecoder : IDisposable
    {
        sealed class Frame
        {
            public int X, Y, W, H;      // sudah di-clip ke kanvas
            public int RawW, RawH;      // ukuran asli di file (untuk decode LZW)
            public int OffX, OffY;      // offset clip relatif ke frame asli
            public bool Interlaced;
            public uint[] Palette;      // 256 entri BGRA
            public int Transparent = -1;
            public int Disposal;
            public int MinCodeSize;
            public byte[] Data;         // sub-block LZW yang sudah disambung
            public int DelayMs;
        }

        readonly List<Frame> frames = new List<Frame>();
        readonly uint bg;
        uint* canvas;                   // W*H piksel BGRA (unmanaged → bisa di-memcpy cepat)
        uint* backup;                   // untuk disposal 3 (restore previous)
        byte[] indices;                 // hasil LZW satu frame (+ slack)
        readonly short[] codeLen = new short[4096];
        readonly int[] codeStart = new int[4096];
        readonly uint[] keepMask = new uint[256], transPal = new uint[256];
        int next;                       // frame berikut yang akan di-decode
        int prevDisposal;
        int prevX, prevY, prevW, prevH;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameCount { get { return frames.Count; } }
        public int NextIndex { get { return next; } }
        public uint* Canvas { get { return canvas; } }
        public int Delay(int i) { return frames[i].DelayMs; }

        public static bool IsGif(byte[] b)
        {
            return b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8';
        }

        public GifDecoder(byte[] file, uint background)
        {
            bg = background | 0xFF000000u;
            Parse(file);
            if (frames.Count == 0) throw new InvalidDataException("GIF tidak berisi frame.");
            long px = (long)Width * Height;
            canvas = (uint*)Marshal.AllocHGlobal(new IntPtr(px * 4));
            if (frames.Exists(f => f.Disposal == 3)) // hanya perlu untuk disposal "restore previous"
                backup = (uint*)Marshal.AllocHGlobal(new IntPtr(px * 4));
            int maxRaw = 1;
            foreach (Frame f in frames) maxRaw = Math.Max(maxRaw, f.RawW * f.RawH);
            indices = new byte[maxRaw + 4096 + 64]; // slack: kode terakhir + salinan 8-byte
            Reset();
        }

        // ── Parsing ──
        void Parse(byte[] b)
        {
            if (!IsGif(b) || b.Length < 13) throw new InvalidDataException("Bukan file GIF.");
            int p = 6;
            Width = b[p] | b[p + 1] << 8; Height = b[p + 2] | b[p + 3] << 8;
            int packed = b[p + 4];
            p += 7;
            if (Width <= 0 || Height <= 0 || (long)Width * Height > 16384L * 16384) throw new InvalidDataException("Ukuran GIF tidak valid.");
            uint[] global = null;
            if ((packed & 0x80) != 0) global = ReadPalette(b, ref p, 2 << (packed & 7));

            int disposal = 0, transparent = -1, delayCs = 0;
            while (p < b.Length)
            {
                int block = b[p++];
                if (block == 0x3B) break; // trailer
                if (block == 0x21)
                {
                    if (p >= b.Length) break;
                    int label = b[p++];
                    if (label == 0xF9 && p + 5 < b.Length && b[p] >= 4)
                    {
                        int gp = b[p + 1];
                        disposal = (gp >> 2) & 7;
                        delayCs = b[p + 2] | b[p + 3] << 8;
                        transparent = (gp & 1) != 0 ? b[p + 4] : -1;
                    }
                    SkipSubBlocks(b, ref p);
                }
                else if (block == 0x2C)
                {
                    if (p + 9 > b.Length) break;
                    var f = new Frame();
                    int fx = b[p] | b[p + 1] << 8, fy = b[p + 2] | b[p + 3] << 8;
                    f.RawW = b[p + 4] | b[p + 5] << 8; f.RawH = b[p + 6] | b[p + 7] << 8;
                    int ip = b[p + 8];
                    p += 9;
                    f.Interlaced = (ip & 0x40) != 0;
                    uint[] pal = (ip & 0x80) != 0 ? ReadPalette(b, ref p, 2 << (ip & 7)) : global;
                    if (pal == null) pal = GrayPalette();
                    if (p >= b.Length) break;
                    f.MinCodeSize = b[p++];
                    f.Data = ReadSubBlocks(b, ref p);
                    f.Palette = pal;
                    f.Transparent = transparent;
                    f.Disposal = disposal;
                    f.DelayMs = delayCs <= 1 ? 100 : delayCs * 10; // sama seperti browser
                    // clip ke kanvas
                    int x0 = Math.Max(0, fx), y0 = Math.Max(0, fy);
                    int x1 = Math.Min(Width, fx + f.RawW), y1 = Math.Min(Height, fy + f.RawH);
                    f.X = x0; f.Y = y0; f.W = Math.Max(0, x1 - x0); f.H = Math.Max(0, y1 - y0);
                    f.OffX = x0 - fx; f.OffY = y0 - fy;
                    if (f.MinCodeSize >= 1 && f.MinCodeSize <= 11 && f.RawW > 0 && f.RawH > 0) frames.Add(f);
                    disposal = 0; transparent = -1; delayCs = 0;
                }
                else break; // byte asing → anggap akhir file
            }
        }

        static uint[] ReadPalette(byte[] b, ref int p, int n)
        {
            var pal = new uint[256];
            for (int i = 0; i < 256; i++) pal[i] = 0xFF000000u;
            for (int i = 0; i < n && p + 2 < b.Length; i++, p += 3)
                pal[i] = 0xFF000000u | (uint)b[p] << 16 | (uint)b[p + 1] << 8 | b[p + 2];
            return pal;
        }

        static uint[] GrayPalette()
        {
            var pal = new uint[256];
            for (uint i = 0; i < 256; i++) pal[i] = 0xFF000000u | i << 16 | i << 8 | i;
            return pal;
        }

        static void SkipSubBlocks(byte[] b, ref int p)
        {
            while (p < b.Length)
            {
                int n = b[p++];
                if (n == 0) return;
                p += n;
            }
        }

        static byte[] ReadSubBlocks(byte[] b, ref int p)
        {
            int start = p, total = 0;
            while (p < b.Length)
            {
                int n = b[p++];
                if (n == 0) break;
                n = Math.Min(n, b.Length - p);
                total += n; p += n;
            }
            var data = new byte[total + 4]; // +4: pembaca bit boleh baca lewat sedikit
            int q = start, o = 0;
            while (o < total)
            {
                int n = Math.Min(b[q++], total - o);
                Buffer.BlockCopy(b, q, data, o, n);
                q += n; o += n;
            }
            return data;
        }

        // ── Decode ──
        public void Reset()
        {
            Fill(0, 0, Width, Height);
            next = 0;
            prevDisposal = 0;
        }

        void Fill(int x, int y, int w, int h)
        {
            for (int r = 0; r < h; r++)
            {
                uint* d = canvas + (long)(y + r) * Width + x;
                for (int c = 0; c < w; c++) d[c] = bg;
            }
        }

        void CopyRect(uint* src, uint* dst, int x, int y, int w, int h)
        {
            for (int r = 0; r < h; r++)
            {
                long o = (long)(y + r) * Width + x;
                Buffer.MemoryCopy(src + o, dst + o, (long)w * 4, (long)w * 4);
            }
        }

        // Decode frame berikutnya ke kanvas. Mengembalikan index frame yang sekarang ada di kanvas.
        public int DecodeNext() { return DecodeNext(null); }

        // Sama, sekaligus menyalin hasilnya ke output (W*H BGRA) bila tidak null.
        public int DecodeNext(uint* output)
        {
            if (next == 0) Fill(0, 0, Width, Height);
            else if (prevDisposal == 2) Fill(prevX, prevY, prevW, prevH);
            else if (prevDisposal == 3) CopyRect(backup, canvas, prevX, prevY, prevW, prevH);

            Frame f = frames[next];
            if (f.Disposal == 3) CopyRect(canvas, backup, f.X, f.Y, f.W, f.H);
            int count = Lzw(f);
            // Frame selebar kanvas → salin tiap baris ke output selagi masih di cache CPU.
            bool fullWidth = f.X == 0 && f.W == Width;
            Composite(f, count, fullWidth ? output : null);
            if (output != null)
            {
                if (!fullWidth) CopyRect(canvas, output, 0, 0, Width, Height);
                else
                {
                    CopyRect(canvas, output, 0, 0, Width, f.Y);
                    CopyRect(canvas, output, 0, f.Y + f.H, Width, Height - f.Y - f.H);
                }
            }

            prevDisposal = f.Disposal;
            prevX = f.X; prevY = f.Y; prevW = f.W; prevH = f.H;
            int shown = next;
            next = (next + 1) % frames.Count;
            return shown;
        }

        // LZW dengan trik "salin dari output sebelumnya": tiap kode = (posisi awal, panjang) di buffer output.
        int Lzw(Frame f)
        {
            int total = f.RawW * f.RawH;
            int min = f.MinCodeSize;
            int clear = 1 << min, eoi = clear + 1;
            int codeSize = min + 1, codeMask = (1 << codeSize) - 1, nextCode = eoi + 1;
            int prevPos = -1, prevLen = 0;
            int p = 0;
            byte[] data = f.Data;
            int dataLen = data.Length - 4;
            fixed (byte* src = data)
            fixed (byte* outp = indices)
            fixed (int* start = codeStart)
            fixed (short* len = codeLen)
            {
                int bytePos = 0;
                uint bitBuf = 0;
                int bits = 0;
                while (p < total)
                {
                    while (bits < codeSize)
                    {
                        if (bytePos >= dataLen) goto done;
                        bitBuf |= (uint)src[bytePos++] << bits;
                        bits += 8;
                    }
                    int code = (int)(bitBuf & (uint)codeMask);
                    bitBuf >>= codeSize; bits -= codeSize;

                    if (code == clear)
                    {
                        codeSize = min + 1; codeMask = (1 << codeSize) - 1; nextCode = eoi + 1;
                        prevPos = -1;
                        continue;
                    }
                    if (code == eoi) break;

                    int L;
                    if (code < clear)
                    {
                        outp[p] = (byte)code; L = 1;
                    }
                    else if (code < nextCode && prevPos >= 0)
                    {
                        L = len[code];
                        byte* s = outp + start[code];
                        byte* d = outp + p;
                        // Salin per 8 byte. Boleh menulis lewat d+L (posisi itu akan ditimpa kode berikut,
                        // dan buffer punya slack); byte yang dibaca untuk d[0..L) selalu sebelum d.
                        if (L <= 32)
                            for (int i = 0; i < L; i += 8) *(ulong*)(d + i) = *(ulong*)(s + i);
                        else Buffer.MemoryCopy(s, d, L, L);
                    }
                    else if (code == nextCode && prevPos >= 0)
                    {
                        // KwKwK: string(prev) + byte pertama string(prev)
                        L = prevLen + 1;
                        byte* s = outp + prevPos;
                        byte* d = outp + p;
                        for (int i = 0; i < L - 1; i++) d[i] = s[i];
                        d[L - 1] = s[0];
                    }
                    else break; // data rusak

                    if (prevPos >= 0 && nextCode < 4096)
                    {
                        start[nextCode] = prevPos;
                        len[nextCode] = (short)(prevLen + 1);
                        nextCode++;
                        if (nextCode > codeMask && codeSize < 12) { codeSize++; codeMask = (1 << codeSize) - 1; }
                    }
                    prevPos = p; prevLen = L;
                    p += L;
                }
            }
            done:
            return Math.Min(p, total);
        }

        void Composite(Frame f, int count, uint* output)
        {
            if (f.W <= 0 || f.H <= 0) return;
            int trans = f.Transparent;
            if (trans >= 0)
            {
                // Tabel branchless: piksel transparan → mask 0xFFFFFFFF (pertahankan isi lama), warna 0.
                for (int i = 0; i < 256; i++) { keepMask[i] = 0; transPal[i] = f.Palette[i]; }
                keepMask[trans] = 0xFFFFFFFFu; transPal[trans] = 0;
            }
            fixed (byte* ind = indices)
            fixed (uint* pal = f.Palette)
            fixed (uint* km = keepMask)
            fixed (uint* tp = transPal)
            {
                for (int r = 0; r < f.H; r++)
                {
                    int rawRow = SourceRow(f, r + f.OffY);
                    long srcOff = (long)rawRow * f.RawW + f.OffX;
                    uint* d = canvas + (long)(f.Y + r) * Width + f.X;
                    int w = (int)Math.Max(0, Math.Min(f.W, count - srcOff)); // data terpotong → sisa baris tetap isi lama
                    byte* s = ind + srcOff;
                    if (w == 0) { }
                    else if (trans < 0)
                    {
                        for (int c = 0; c < w; c++) d[c] = pal[s[c]];
                    }
                    else
                    {
                        for (int c = 0; c < w; c++)
                        {
                            int i = s[c];
                            d[c] = (d[c] & km[i]) | tp[i];
                        }
                    }
                    if (output != null)
                    {
                        long o = (long)(f.Y + r) * Width; // dipanggil hanya untuk frame selebar kanvas
                        Buffer.MemoryCopy(canvas + o, output + o, (long)Width * 4, (long)Width * 4);
                    }
                }
            }
        }

        // Baris ke-r di gambar → baris ke berapa di stream (interlace GIF: 4 pass).
        static int SourceRow(Frame f, int r)
        {
            if (!f.Interlaced) return r;
            int h = f.RawH;
            int n1 = (h + 7) / 8, n2 = (h + 3) / 8, n3 = (h + 1) / 4;
            if (r % 8 == 0) return r / 8;
            if (r % 8 == 4) return n1 + r / 8;
            if (r % 4 == 2) return n1 + n2 + r / 4;
            return n1 + n2 + n3 + r / 2;
        }

        public void Dispose()
        {
            if (canvas != null) { Marshal.FreeHGlobal((IntPtr)canvas); canvas = null; }
            if (backup != null) { Marshal.FreeHGlobal((IntPtr)backup); backup = null; }
        }
    }
}
