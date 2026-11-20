// GIF Wallpaper — pasang GIF animasi sebagai live wallpaper Windows.
// Dikompilasi dengan csc.exe bawaan .NET Framework 4.x (C# 5), tanpa dependensi tambahan.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GifWallpaper
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (Array.Exists(args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                Uninstaller.Run(Array.Exists(args, a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\GifWallpaper.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("GIF Wallpaper sudah berjalan.\nCek ikonnya di system tray (pojok kanan bawah).",
                        "GIF Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Per-monitor DPI aware supaya koordinat monitor = piksel asli.
                bool dpiOk = false;
                try { dpiOk = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                if (!dpiOk) { try { Native.SetProcessDPIAware(); } catch { } }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Write("=== GIF Wallpaper mulai (" + Environment.OSVersion.VersionString + ")" + (args.Length > 0 ? " " + string.Join(" ", args) : ""));
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("CRASH: " + e.ExceptionObject);
                    DesktopHost.RefreshWallpaper();
                };
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("Error: " + e.Exception);
                    MessageBox.Show("Terjadi error: " + e.Exception.Message, "GIF Wallpaper",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                bool startInTray = Array.Exists(args, a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                var form = new MainForm();
                if (startInTray)
                {
                    form.StartInTray();
                    Application.Run();
                }
                else
                {
                    Application.Run(form);
                }
            }
        }
    }

    enum FitMode { Fill, Fit, Stretch, Center, Tile }

    // Log kecil di %APPDATA%\GifWallpaper\log.txt untuk melacak kapan & kenapa wallpaper bermasalah.
    static class Log
    {
        static readonly object gate = new object();

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GifWallpaper"); }
        }

        public static string FilePath { get { return Path.Combine(Dir, "log.txt"); } }

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = Path.Combine(Dir, "log.old.txt");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ───────────────────────────── Pengaturan ─────────────────────────────
    class Settings
    {
        public string GifPath = "";
        public FitMode Mode = FitMode.Fill;
        public int Monitor = -1; // -1 = semua monitor
        public Color Background = Color.Black;
        public int SpeedStep = 4; // 1..12, x0.25
        public bool PauseWhenCovered = true;
        public bool Active = false;

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GifWallpaper", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int n;
                    switch (k)
                    {
                        case "gif": s.GifPath = v; break;
                        case "mode": if (int.TryParse(v, out n) && Enum.IsDefined(typeof(FitMode), n)) s.Mode = (FitMode)n; break;
                        case "monitor": if (int.TryParse(v, out n)) s.Monitor = n; break;
                        case "background": try { s.Background = ColorTranslator.FromHtml(v); } catch { } break;
                        case "speed": if (int.TryParse(v, out n) && n >= 1 && n <= 12) s.SpeedStep = n; break;
                        case "pauseCovered": s.PauseWhenCovered = v == "1"; break;
                        case "active": s.Active = v == "1"; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "gif=" + GifPath,
                    "mode=" + (int)Mode,
                    "monitor=" + Monitor,
                    "background=" + ColorTranslator.ToHtml(Color.FromArgb(Background.R, Background.G, Background.B)),
                    "speed=" + SpeedStep,
                    "pauseCovered=" + (PauseWhenCovered ? "1" : "0"),
                    "active=" + (Active ? "1" : "0"),
                }, Encoding.UTF8);
            }
            catch { }
        }
    }

    // ───────────────────────────── Player GIF ─────────────────────────────
    // GIF kecil: semua frame di-decode sekali ke DIB (mode cepat).
    // GIF besar: data terkompresi di RAM (≈ ukuran file), thread latar men-decode 2 frame di depan
    // ke ring buffer, sekaligus men-scale-nya ke ukuran monitor. Thread UI tinggal BitBlt.
    unsafe sealed class GifPlayer : IDisposable
    {
        const long CacheBudget = 768L * 1024 * 1024;
        const int SlotCount = 3; // 1 tampil + 2 siap

        sealed class Slot
        {
            public IntPtr Bmp; public uint* Bits; public int Index;
            public IntPtr[] Scaled = new IntPtr[0]; public Size[] ScaledSize = new Size[0]; public int ScaleVersion = -1;
        }

        readonly GifDecoder decoder;
        readonly int[] delays;
        IntPtr[] cache;                       // mode cepat
        int index;

        // mode streaming
        readonly object gate = new object();
        Slot[] slots;
        readonly Queue<Slot> free = new Queue<Slot>(), ready = new Queue<Slot>();
        Slot current;
        Thread worker;
        volatile bool stopping;
        Rectangle[] jobSrc = new Rectangle[0];
        Size[] jobDst = new Size[0];
        int jobVersion;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameCount { get; private set; }
        public string Name { get; private set; }
        public bool AllFramesCached { get { return cache != null; } }

        public GifPlayer(string path, Color background)
        {
            Color bg = Color.FromArgb(background.R, background.G, background.B);
            Name = Path.GetFileName(path);
            byte[] file = File.ReadAllBytes(path);

            if (!GifDecoder.IsGif(file))
            {
                // Bukan GIF (PNG/JPG/BMP): gambar diam lewat GDI+.
                using (var ms = new MemoryStream(file))
                {
                    Bitmap img;
                    try { img = new Bitmap(ms); }
                    catch (ArgumentException) { throw new InvalidDataException("File ini bukan gambar/GIF yang valid."); }
                    using (img) { Width = img.Width; Height = img.Height; cache = new[] { img.GetHbitmap(bg) }; }
                }
                FrameCount = 1; delays = new[] { 100 };
                return;
            }

            decoder = new GifDecoder(file, (uint)(bg.R << 16 | bg.G << 8 | bg.B));
            file = null;
            Width = decoder.Width; Height = decoder.Height; FrameCount = decoder.FrameCount;
            delays = new int[FrameCount];
            for (int i = 0; i < FrameCount; i++) delays[i] = decoder.Delay(i);

            long bytes = (long)Width * Height * 4 * FrameCount;
            if (bytes <= CacheBudget && FrameCount <= 4000)
            {
                cache = new IntPtr[FrameCount];
                try
                {
                    for (int i = 0; i < FrameCount; i++)
                    {
                        uint* bits;
                        cache[i] = CreateDib(Width, Height, out bits);
                        decoder.DecodeNext(bits);
                    }
                }
                catch { Dispose(); throw; }
                decoder.Dispose(); // kanvas tidak dipakai lagi
                return;
            }

            slots = new Slot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                var s = new Slot();
                s.Bmp = CreateDib(Width, Height, out s.Bits);
                slots[i] = s;
            }
            current = slots[0];
            current.Index = decoder.DecodeNext(current.Bits);
            for (int i = 1; i < SlotCount; i++) free.Enqueue(slots[i]);
            worker = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "GIF decode" };
            worker.Start();
        }

        static IntPtr CreateDib(int w, int h, out uint* bits)
        {
            var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr pv;
            IntPtr bmp = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out pv, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) throw new OutOfMemoryException("Memori tidak cukup untuk frame GIF.");
            bits = (uint*)pv;
            return bmp;
        }
        public IntPtr Current { get { return cache != null ? cache[index] : current.Bmp; } }
        public int Index { get { return cache != null ? index : current.Index; } }
        public int CurrentDelay { get { return delays[Index]; } }

        // Maju ke frame berikut. false = frame belum siap (thread decode tertinggal) → coba lagi sebentar.
        public bool Next()
        {
            if (FrameCount <= 1) return true;
            if (cache != null) { index = (index + 1) % FrameCount; return true; }
            lock (gate)
            {
                if (ready.Count == 0) return false;
                free.Enqueue(current);
                current = ready.Dequeue();
                Monitor.PulseAll(gate);
                return true;
            }
        }

        // Window memberi tahu bagian frame mana yang perlu di-scale ke ukuran berapa (per monitor).
        public int SetScaleJobs(Rectangle[] src, Size[] dst)
        {
            lock (gate)
            {
                bool same = src.Length == jobSrc.Length;
                for (int k = 0; same && k < src.Length; k++) same = src[k] == jobSrc[k] && dst[k] == jobDst[k];
                if (!same) { jobSrc = src; jobDst = dst; jobVersion++; } // jendela pengganti → frame siap tetap terpakai
                return jobVersion;
            }
        }

        // Frame saat ini yang sudah di-scale untuk job k (Zero = belum ada → scale sendiri).
        public IntPtr CurrentScaled(int version, int k)
        {
            if (cache != null) return IntPtr.Zero;
            Slot s = current;
            if (s.ScaleVersion != version || k >= s.Scaled.Length) return IntPtr.Zero;
            return s.Scaled[k];
        }

        void WorkerLoop()
        {
            IntPtr srcDc = Native.CreateCompatibleDC(IntPtr.Zero), dstDc = Native.CreateCompatibleDC(IntPtr.Zero);
            Native.SetStretchBltMode(dstDc, Native.HALFTONE);
            Native.SetBrushOrgEx(dstDc, 0, 0, IntPtr.Zero);
            try
            {
                while (true)
                {
                    Slot s; Rectangle[] src; Size[] dst; int ver;
                    lock (gate)
                    {
                        while (free.Count == 0 && !stopping) Monitor.Wait(gate);
                        if (stopping) return;
                        s = free.Dequeue();
                        src = jobSrc; dst = jobDst; ver = jobVersion;
                    }
                    s.Index = decoder.DecodeNext(s.Bits);
                    if (src.Length > 0) ScaleInto(s, src, dst, srcDc, dstDc);
                    s.ScaleVersion = ver;
                    lock (gate)
                    {
                        if (stopping) return;
                        ready.Enqueue(s);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Thread decode error: " + ex); }
            finally { Native.DeleteDC(srcDc); Native.DeleteDC(dstDc); }
        }

        void ScaleInto(Slot s, Rectangle[] src, Size[] dst, IntPtr srcDc, IntPtr dstDc)
        {
            if (s.Scaled.Length != src.Length)
            {
                FreeScaled(s);
                s.Scaled = new IntPtr[src.Length]; s.ScaledSize = new Size[src.Length];
            }
            IntPtr o1 = Native.SelectObject(srcDc, s.Bmp);
            for (int k = 0; k < src.Length; k++)
            {
                if (dst[k].Width <= 0 || dst[k].Height <= 0) continue;
                if (s.Scaled[k] == IntPtr.Zero || s.ScaledSize[k] != dst[k])
                {
                    if (s.Scaled[k] != IntPtr.Zero) Native.DeleteObject(s.Scaled[k]);
                    uint* unused;
                    s.Scaled[k] = CreateDib(dst[k].Width, dst[k].Height, out unused);
                    s.ScaledSize[k] = dst[k];
                }
                IntPtr o2 = Native.SelectObject(dstDc, s.Scaled[k]);
                Rectangle r = src[k];
                Native.StretchBlt(dstDc, 0, 0, dst[k].Width, dst[k].Height, srcDc, r.X, r.Y, r.Width, r.Height, Native.SRCCOPY);
                Native.SelectObject(dstDc, o2);
            }
            Native.SelectObject(srcDc, o1);
            Native.GdiFlush();
        }

        static void FreeScaled(Slot s)
        {
            foreach (IntPtr h in s.Scaled) if (h != IntPtr.Zero) Native.DeleteObject(h);
            s.Scaled = new IntPtr[0]; s.ScaledSize = new Size[0];
        }

        public void Dispose()
        {
            if (worker != null)
            {
                lock (gate) { stopping = true; Monitor.PulseAll(gate); }
                worker.Join(2000);
                worker = null;
            }
            if (slots != null)
            {
                foreach (Slot s in slots) { FreeScaled(s); if (s.Bmp != IntPtr.Zero) Native.DeleteObject(s.Bmp); s.Bmp = IntPtr.Zero; }
                slots = null;
            }
            if (cache != null)
            {
                foreach (IntPtr h in cache) if (h != IntPtr.Zero) Native.DeleteObject(h);
                cache = null;
            }
            if (decoder != null) decoder.Dispose();
        }
    }

    // ───────────────────────── Tempel ke desktop (di belakang ikon) ─────────────────────────
    // Susunan lapisan desktop saat wallpaper dipasang — dipakai watchdog untuk mendeteksi perubahan.
    class DesktopLayout
    {
        public bool Raised;
        public IntPtr Progman, DefView, WorkerW, Parent;
        public HashSet<IntPtr> KnownWorkers = new HashSet<IntPtr>();
        public override string ToString()
        {
            return string.Format("{0} progman={1} defview={2} workerw={3}",
                Raised ? "24H2+" : "klasik", Progman, DefView, WorkerW);
        }
    }

    static class DesktopHost
    {
        public static bool Attach(IntPtr hwnd, Rectangle screenRect, out string info, out DesktopLayout layout)
        {
            layout = null;
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) { info = "jendela desktop (Progman) tidak ditemukan"; return false; }

            // Minta Explorer membuat lapisan WorkerW di belakang ikon desktop.
            IntPtr res;
            Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out res);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                // Windows 11 24H2 ke atas: WorkerW & SHELLDLL_DefView jadi anak Progman.
                IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                IntPtr childWorker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (defView != IntPtr.Zero && childWorker != IntPtr.Zero)
                {
                    // PENTING: WS_EX_LAYERED harus dipasang SETELAH SetParent. Kalau sebelumnya,
                    // DWM tidak pernah menerima isi jendela ini (teruji: 0 piksel vs penuh).
                    MakeChild(hwnd);
                    Native.SetParent(hwnd, progman);
                    int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                    Native.SetLayeredWindowAttributes(hwnd, 0, 255, Native.LWA_ALPHA);
                    Place(hwnd, progman, screenRect, defView); // tepat di bawah ikon
                    layout = new DesktopLayout { Raised = true, Progman = progman, DefView = defView, WorkerW = childWorker, Parent = progman };
                    // Semua WorkerW (saat transisi slideshow bisa ada 2) → paling bawah, dan dicatat.
                    foreach (IntPtr w in ChildWorkers(progman))
                    {
                        Native.SetWindowPos(w, new IntPtr(1) /*HWND_BOTTOM*/, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        layout.KnownWorkers.Add(w);
                    }
                    info = "mode Windows 11 24H2+";
                    return true;
                }

                // Windows 10 / Windows 11 lama: WorkerW top-level setelah jendela yang memuat ikon.
                IntPtr worker = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr top, IntPtr lp)
                {
                    if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                        worker = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return true;
                }, IntPtr.Zero);
                if (worker != IntPtr.Zero)
                {
                    MakeChild(hwnd);
                    Native.SetParent(hwnd, worker);
                    Place(hwnd, worker, screenRect, IntPtr.Zero);
                    info = "mode WorkerW klasik";
                    layout = new DesktopLayout { Raised = false, Progman = progman, WorkerW = worker, Parent = worker };
                    return true;
                }
                Thread.Sleep(100);
            }
            info = "lapisan WorkerW di belakang ikon desktop tidak muncul";
            return false;
        }

        static void MakeChild(IntPtr hwnd)
        {
            int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
            style = (style & ~Native.WS_POPUP) | Native.WS_CHILD;
            Native.SetWindowLong(hwnd, Native.GWL_STYLE, style);
        }

        static void Place(IntPtr hwnd, IntPtr parent, Rectangle screenRect, IntPtr insertAfter)
        {
            var pt = new Native.POINT { X = screenRect.X, Y = screenRect.Y };
            Native.MapWindowPoints(IntPtr.Zero, parent, ref pt, 1);
            Native.SetWindowPos(hwnd, insertAfter, pt.X, pt.Y, screenRect.Width, screenRect.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | 0x20 /*SWP_FRAMECHANGED*/);
        }

        // Paksa Explorer menggambar ulang wallpaper aslinya (tanpa mengubah pengaturan tersimpan).
        // Windows 11 24H2+: Progman memakai WS_EX_NOREDIRECTIONBITMAP ("raised desktop").
        public static bool IsRaisedDesktop()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            return progman != IntPtr.Zero &&
                (Native.GetWindowLong(progman, Native.GWL_EXSTYLE) & 0x00200000 /*WS_EX_NOREDIRECTIONBITMAP*/) != 0;
        }

        // null = sehat; selain itu alasan wallpaper perlu dipasang ulang.
        public static string CheckLayout(IntPtr hwnd, DesktopLayout layout)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return "jendela wallpaper hilang";
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != layout.Progman) return "Progman berubah (Explorer di-restart)";
            if (Native.GetParent(hwnd) != layout.Parent) return "parent jendela berubah";
            if (!Native.IsWindowVisible(hwnd)) return "jendela wallpaper tersembunyi";
            if (layout.Raised)
            {
                if (!IsRaisedDesktop()) return "mode desktop berubah";
                IntPtr dv = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != layout.DefView) return "lapisan ikon desktop dibuat ulang";
                foreach (IntPtr w in ChildWorkers(progman))
                    if (!layout.KnownWorkers.Contains(w)) return "WorkerW baru dibuat (mis. slideshow ganti gambar)";
                if (Native.GetWindow(dv, 2 /*GW_HWNDNEXT*/) != hwnd) return "urutan lapisan desktop berubah";
            }
            return null;
        }

        static List<IntPtr> ChildWorkers(IntPtr progman)
        {
            var list = new List<IntPtr>();
            IntPtr w = IntPtr.Zero;
            while ((w = Native.FindWindowEx(progman, w, "WorkerW", null)) != IntPtr.Zero && list.Count < 16) list.Add(w);
            return list;
        }

        public static void RefreshWallpaper()
        {
            // Di raised desktop, WorkerW di bawah kita sudah menggambar wallpaper asli;
            // SPI_SETDESKWALLPAPER justru menghancurkan WorkerW itu.
            if (IsRaisedDesktop()) return;
            try
            {
                var sb = new StringBuilder(1024);
                if (Native.SystemParametersInfo(0x0073 /*SPI_GETDESKWALLPAPER*/, (uint)sb.Capacity, sb, 0))
                    Native.SystemParametersInfo(0x0014 /*SPI_SETDESKWALLPAPER*/, 0, sb, 0);
            }
            catch { }
        }
    }

    // ───────────────────────── Pembangun frame presisi ─────────────────────────
    // Thread kecil yang tidur di waitable timer resolusi tinggi (Windows 10 1803+) lalu mem-post
    // pesan ke jendela wallpaper tepat saat frame berikut jatuh tempo. Tanpa timeBeginPeriod global.
    sealed class FramePacer : IDisposable
    {
        sealed class NativeWait : WaitHandle
        {
            public NativeWait(IntPtr h) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, true); }
        }

        readonly IntPtr hwnd;
        readonly int msg;
        readonly Stopwatch clock;
        readonly object gate = new object();
        readonly AutoResetEvent kick = new AutoResetEvent(false);
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly NativeWait timer; // null → fallback ke WaitOne(ms)
        readonly IntPtr timerHandle;
        readonly Thread thread;
        double target = -1;

        public FramePacer(IntPtr hwnd, int msg, Stopwatch clock)
        {
            this.hwnd = hwnd; this.msg = msg; this.clock = clock;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
                if (h == IntPtr.Zero) h = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            }
            catch { }
            if (h != IntPtr.Zero) { timer = new NativeWait(h); timerHandle = h; }
            thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Frame pacer" };
            thread.Start();
        }

        public void WakeAt(double ms)
        {
            lock (gate) target = ms;
            kick.Set();
        }

        void Loop()
        {
            var waits = timer != null ? new WaitHandle[] { stop, kick, timer } : new WaitHandle[] { stop, kick };
            while (true)
            {
                double t;
                lock (gate) t = target;
                if (t < 0)
                {
                    if (WaitHandle.WaitAny(waits, Timeout.Infinite) == 0) return;
                    continue;
                }
                double rem = t - clock.Elapsed.TotalMilliseconds;
                if (rem > 0.2)
                {
                    int r;
                    if (timer != null)
                    {
                        long dueTime = -(long)(rem * 10000); // relatif, satuan 100 ns
                        Native.SetWaitableTimer(timerHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                        r = WaitHandle.WaitAny(waits, Timeout.Infinite);
                    }
                    else r = WaitHandle.WaitAny(waits, (int)Math.Ceiling(rem));
                    if (r == 0) return;
                    if (r == 1) continue; // target berubah
                    if (clock.Elapsed.TotalMilliseconds < t - 0.2) continue; // bangun terlalu cepat
                }
                lock (gate)
                {
                    if (target != t) continue;
                    target = -1;
                }
                Native.PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            stop.Set();
            thread.Join(500);
            if (timer != null) timer.Dispose();
            kick.Dispose();
            stop.Dispose();
        }
    }

    // ───────────────────────── Jendela wallpaper ─────────────────────────
    class WallpaperWindow : Form
    {
        readonly GifPlayer player;
        readonly Rectangle screenRect;
        readonly Rectangle[] monitors; // relatif ke jendela ini
        readonly IntPtr bgBrush;
        readonly Stopwatch clock = Stopwatch.StartNew();
        FitMode mode;
        double speed;
        bool paused, attached, ownsPlayer;
        double due;
        DesktopLayout layout;

        public WallpaperWindow(GifPlayer player, Rectangle[] screens, FitMode mode, Color bg, double speed, bool ownsPlayer)
        {
            this.player = player;
            this.ownsPlayer = ownsPlayer;
            this.mode = mode;
            this.speed = speed;

            Rectangle u = screens[0];
            foreach (Rectangle s in screens) u = Rectangle.Union(u, s);
            screenRect = u;
            monitors = Array.ConvertAll(screens, s => new Rectangle(s.X - u.X, s.Y - u.Y, s.Width, s.Height));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = u;
            BackColor = bg;
            Text = "GIF Wallpaper Surface";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            bgBrush = Native.CreateSolidBrush(ColorTranslator.ToWin32(bg));
            ResetScaledCache();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public bool AttachToDesktop(out string info)
        {
            IntPtr h = Handle; // buat handle tanpa menampilkan jendela top-level
            if (!DesktopHost.Attach(h, screenRect, out info, out layout)) return false;
            info += " · " + layout;
            attached = true;
            Invalidate();
            if (!paused) StartClock();
            return true;
        }

        public GifPlayer Player { get { return player; } }

        // Serah-terima player ke jendela pengganti (tanpa memuat ulang GIF).
        public void ReleasePlayer() { ownsPlayer = false; StopPacer(); }
        public void TakePlayerOwnership() { ownsPlayer = true; }

        public string CheckHealth()
        {
            if (!attached || !IsHandleCreated || layout == null) return "jendela wallpaper hilang";
            return DesktopHost.CheckLayout(Handle, layout);
        }

        // Bandingkan yang seharusnya tergambar dengan yang benar-benar dipegang DWM.
        // true = DWM kosong/hitam padahal frame GIF berisi → jendela perlu dipasang ulang.
        public bool InvisibleToDwm()
        {
            if (!attached || !IsHandleCreated) return false;
            Rectangle m = monitors[0];
            int w = m.Right, h = m.Bottom;
            using (var actual = new Bitmap(w, h))
            using (var expected = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(actual))
                {
                    IntPtr dc = g.GetHdc();
                    try { Native.PrintWindow(Handle, dc, 2 /*PW_RENDERFULLCONTENT*/); }
                    finally { g.ReleaseHdc(dc); }
                }
                using (Graphics g = Graphics.FromImage(expected))
                {
                    IntPtr dc = g.GetHdc();
                    try { Render(dc, true); }
                    finally { g.ReleaseHdc(dc); }
                }
                int total = 0, actualLit = 0, expectedLit = 0;
                int step = Math.Max(8, Math.Min(m.Width, m.Height) / 30);
                for (int y = m.Top + step / 2; y < m.Bottom; y += step)
                    for (int x = m.Left + step / 2; x < m.Right; x += step)
                    {
                        total++;
                        Color a = actual.GetPixel(x, y), e = expected.GetPixel(x, y);
                        if (a.R + a.G + a.B > 30) actualLit++;
                        if (e.R + e.G + e.B > 30) expectedLit++;
                    }
                return total > 0 && expectedLit > total / 5 && actualLit < total / 50;
            }
        }

        public FitMode Mode { set { mode = value; ResetScaledCache(); if (IsHandleCreated) Invalidate(); } }
        public double Speed { set { speed = value; } }

        public bool Paused
        {
            get { return paused; }
            set
            {
                if (paused == value) return;
                paused = value;
                if (paused) { StopPacer(); return; }
                if (IsHandleCreated) Invalidate(); // gambar ulang penuh, jaga-jaga isi sempat dibuang
                StartClock();
            }
        }

        // ── Penjadwalan frame ──
        // Timer WinForms beresolusi ~15,6 ms, jadi GIF cepat (atau kecepatan 2×/3×) melambat diam-diam.
        // FramePacer membangunkan thread UI tepat waktu (timer resolusi tinggi), dan fps dibatasi
        // refresh rate monitor (maks 60): frame yang tak sempat tampil dilewati tanpa digambar.
        void StartClock()
        {
            due = clock.Elapsed.TotalMilliseconds + player.CurrentDelay / speed;
            WakeAt(due);
        }

        void WakeAt(double ms)
        {
            if (paused || !attached || !IsHandleCreated || player.FrameCount <= 1) return;
            if (pacer == null) pacer = new FramePacer(Handle, WM_FRAME, clock);
            pacer.WakeAt(ms);
        }

        void OnFrameDue()
        {
            if (paused || !attached || !IsHandleCreated) return;
            double now = clock.Elapsed.TotalMilliseconds;
            if (!player.Next())
            {
                statMiss++; // thread decode tertinggal → coba lagi sebentar, jadwal tidak dimajukan
                WakeAt(now + 3);
                return;
            }
            due += player.CurrentDelay / speed;
            statAdvanced++;
            // Tertinggal lebih dari satu frame → lewati frame (tetap di-decode, tidak digambar).
            for (int skip = 0; due <= now && skip < 8 && player.Next(); skip++)
            {
                due += player.CurrentDelay / speed;
                statAdvanced++;
            }
            if (due < now - 250) due = now; // tertinggal jauh (mis. setelah sleep) → jangan kejar

            IntPtr hdc = Native.GetDC(Handle);
            try { Render(hdc, false); }
            finally { Native.ReleaseDC(Handle, hdc); }
            statFrames++;
            if (Stats && now - statStart >= 10000)
            {
                double sec = (now - statStart) / 1000.0;
                Log.Write(string.Format("stats: tampil {0:0.0} fps, animasi {1:0.0} frame/dtk, {2} kali frame belum siap",
                    statFrames / sec, statAdvanced / sec, statMiss));
                statFrames = 0; statAdvanced = 0; statMiss = 0; statStart = now;
            }
            WakeAt(Math.Max(due, now + MinFrameMs));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_FRAME) { OnFrameDue(); return; }
            base.WndProc(ref m);
        }

        void StopPacer()
        {
            if (pacer != null) { pacer.Dispose(); pacer = null; }
        }

        const int WM_FRAME = 0x8000 + 0x47; // WM_APP + n
        static readonly double MinFrameMs = ComputeMinFrameMs();
        FramePacer pacer;

        static double ComputeMinFrameMs()
        {
            int hz = 60;
            try
            {
                IntPtr dc = Native.GetDC(IntPtr.Zero);
                int v = Native.GetDeviceCaps(dc, 116 /*VREFRESH*/);
                Native.ReleaseDC(IntPtr.Zero, dc);
                if (v > 1) hz = Math.Min(60, v);
            }
            catch { }
            return 1000.0 / hz - 0.5; // sedikit toleransi supaya tidak meleset satu vsync
        }

        // Set GIFWALLPAPER_STATS=1 untuk mencatat fps ke log tiap 10 detik (diagnostik).
        static readonly bool Stats = Environment.GetEnvironmentVariable("GIFWALLPAPER_STATS") == "1";
        int statFrames, statAdvanced, statMiss;
        double statStart;

        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr hdc = e.Graphics.GetHdc();
            try { Render(hdc, true); }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            StopPacer();
            attached = false;
            base.OnHandleDestroyed(e);
        }

        // ── Cache frame yang sudah di-scale ──
        // Stretch HALFTONE ~10 ms/frame di 1080p; BitBlt frame yang sudah di-scale ~1-2 ms.
        // Jadi tiap frame di-scale sekali (saat putaran pertama), lalu dipakai ulang.
        const long ScaledBudget = 512L * 1024 * 1024;
        IntPtr[][] scaled; // [monitor][frame]
        int scaleVersion = -1;

        void ResetScaledCache()
        {
            FreeScaledCache();
            if (!player.AllFramesCached)
            {
                // Mode streaming: scaling dikerjakan thread decode, bukan thread UI.
                var src = new Rectangle[monitors.Length];
                var dst = new Size[monitors.Length];
                if (mode != FitMode.Tile)
                    for (int k = 0; k < monitors.Length; k++)
                    {
                        BlitParams b = Compute(DestRect(monitors[k], player.Width, player.Height), monitors[k], player.Width, player.Height);
                        if (b.Valid && (b.SW != b.DW || b.SH != b.DH))
                        {
                            src[k] = new Rectangle(b.SX, b.SY, b.SW, b.SH);
                            dst[k] = new Size(b.DW, b.DH);
                        }
                    }
                scaleVersion = player.SetScaleJobs(src, dst);
                return;
            }
            if (mode == FitMode.Tile) return;
            long perFrame = 0;
            foreach (Rectangle m in monitors)
            {
                BlitParams b = Compute(DestRect(m, player.Width, player.Height), m, player.Width, player.Height);
                if (b.Valid) perFrame += (long)b.DW * b.DH * 4;
            }
            if (perFrame * player.FrameCount > ScaledBudget || player.FrameCount * monitors.Length > 6000) return;
            scaled = new IntPtr[monitors.Length][];
            for (int k = 0; k < monitors.Length; k++) scaled[k] = new IntPtr[player.FrameCount];
        }

        void FreeScaledCache()
        {
            if (scaled == null) return;
            foreach (IntPtr[] arr in scaled)
                foreach (IntPtr h in arr)
                    if (h != IntPtr.Zero) Native.DeleteObject(h);
            scaled = null;
        }

        struct BlitParams { public bool Valid; public int DX, DY, DW, DH, SX, SY, SW, SH; }

        // Peta rect tujuan d (yang memuat seluruh sumber) → bagian yang jatuh di monitor m.
        static BlitParams Compute(RectangleF d, Rectangle m, int sw, int sh)
        {
            var b = new BlitParams();
            int dx = (int)Math.Round(Math.Max(d.Left, m.Left));
            int dy = (int)Math.Round(Math.Max(d.Top, m.Top));
            int dr = (int)Math.Round(Math.Min(d.Right, m.Right));
            int db = (int)Math.Round(Math.Min(d.Bottom, m.Bottom));
            if (dr <= dx || db <= dy) return b;
            double kx = sw / (double)d.Width, ky = sh / (double)d.Height;
            b.DX = dx; b.DY = dy; b.DW = dr - dx; b.DH = db - dy;
            b.SX = (int)Math.Round((dx - d.X) * kx);
            b.SY = (int)Math.Round((dy - d.Y) * ky);
            b.SW = Math.Max(1, Math.Min(sw - b.SX, (int)Math.Round(b.DW * kx)));
            b.SH = Math.Max(1, Math.Min(sh - b.SY, (int)Math.Round(b.DH * ky)));
            b.Valid = true;
            return b;
        }

        void Render(IntPtr hdc, bool full)
        {
            IntPtr src = player.Current;
            if (src == IntPtr.Zero) return;
            IntPtr mem = Native.CreateCompatibleDC(hdc);
            IntPtr old = Native.SelectObject(mem, src);
            IntPtr cacheDc = Native.CreateCompatibleDC(hdc);
            Native.SetStretchBltMode(hdc, Native.HALFTONE);
            Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            int sw = player.Width, sh = player.Height, idx = player.Index;

            for (int k = 0; k < monitors.Length; k++)
            {
                Rectangle m = monitors[k];
                if (mode == FitMode.Tile)
                {
                    for (int y = m.Top; y < m.Bottom; y += sh)
                        for (int x = m.Left; x < m.Right; x += sw)
                            Draw(hdc, mem, Compute(new RectangleF(x, y, sw, sh), m, sw, sh));
                    continue;
                }

                RectangleF d = DestRect(m, sw, sh);
                if (full)
                {
                    // Isi area kosong (letterbox) dengan warna latar, tanpa menimpa area gambar → tidak berkedip.
                    RectangleF vis = RectangleF.Intersect(d, m);
                    Native.SaveDC(hdc);
                    if (!vis.IsEmpty)
                        Native.ExcludeClipRect(hdc, (int)Math.Round(vis.Left), (int)Math.Round(vis.Top),
                            (int)Math.Round(vis.Right), (int)Math.Round(vis.Bottom));
                    var r = new Native.RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Bottom };
                    Native.FillRect(hdc, ref r, bgBrush);
                    Native.RestoreDC(hdc, -1);
                }
                BlitParams b = Compute(d, m, sw, sh);
                if (!b.Valid) continue;
                bool needsScale = b.SW != b.DW || b.SH != b.DH;
                IntPtr pre = needsScale ? player.CurrentScaled(scaleVersion, k) : IntPtr.Zero;
                if (pre != IntPtr.Zero)
                {
                    IntPtr o3 = Native.SelectObject(cacheDc, pre);
                    Native.BitBlt(hdc, b.DX, b.DY, b.DW, b.DH, cacheDc, 0, 0, Native.SRCCOPY);
                    Native.SelectObject(cacheDc, o3);
                }
                else if (needsScale && scaled != null)
                {
                    IntPtr cached = scaled[k][idx];
                    if (cached == IntPtr.Zero)
                    {
                        cached = Native.CreateCompatibleBitmap(hdc, b.DW, b.DH);
                        if (cached == IntPtr.Zero) { FreeScaledCache(); Draw(hdc, mem, b); continue; } // memori habis → mode live
                        IntPtr o = Native.SelectObject(cacheDc, cached);
                        Native.SetStretchBltMode(cacheDc, Native.HALFTONE);
                        Native.SetBrushOrgEx(cacheDc, 0, 0, IntPtr.Zero);
                        Native.StretchBlt(cacheDc, 0, 0, b.DW, b.DH, mem, b.SX, b.SY, b.SW, b.SH, Native.SRCCOPY);
                        Native.SelectObject(cacheDc, o);
                        scaled[k][idx] = cached;
                    }
                    IntPtr o2 = Native.SelectObject(cacheDc, cached);
                    Native.BitBlt(hdc, b.DX, b.DY, b.DW, b.DH, cacheDc, 0, 0, Native.SRCCOPY);
                    Native.SelectObject(cacheDc, o2);
                }
                else
                {
                    Draw(hdc, mem, b);
                }
            }

            Native.DeleteDC(cacheDc);
            Native.SelectObject(mem, old);
            Native.DeleteDC(mem);
        }

        static void Draw(IntPtr hdc, IntPtr mem, BlitParams b)
        {
            if (!b.Valid) return;
            if (b.SW == b.DW && b.SH == b.DH)
                Native.BitBlt(hdc, b.DX, b.DY, b.DW, b.DH, mem, b.SX, b.SY, Native.SRCCOPY);
            else
                Native.StretchBlt(hdc, b.DX, b.DY, b.DW, b.DH, mem, b.SX, b.SY, b.SW, b.SH, Native.SRCCOPY);
        }

        RectangleF DestRect(Rectangle m, int sw, int sh)
        {
            float s;
            switch (mode)
            {
                case FitMode.Stretch: return m;
                case FitMode.Fit: s = Math.Min(m.Width / (float)sw, m.Height / (float)sh); break;
                case FitMode.Fill: s = Math.Max(m.Width / (float)sw, m.Height / (float)sh); break;
                default: s = 1f; break; // Center
            }
            float w = sw * s, h = sh * s;
            return new RectangleF(m.X + (m.Width - w) / 2f, m.Y + (m.Height - h) / 2f, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopPacer();
                FreeScaledCache();
                if (ownsPlayer) player.Dispose();
                Native.DeleteObject(bgBrush);
            }
            base.Dispose(disposing);
        }
    }

    // ───────────────────────── Jendela utama ─────────────────────────
    class MainForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "GifWallpaper";

        readonly Settings settings = Settings.Load();
        readonly Icon appIcon = MakeIcon();
        readonly int taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        readonly int exitMsg = Native.RegisterWindowMessage(InstallInfo.ExitMessage);

        WallpaperWindow wallpaper;
        bool userPaused, autoPaused, busy, loadingUi, reallyExit, autoStarted, trayHintShown;
        MemoryStream previewStream;

        Panel previewPanel;
        PictureBox preview;
        Label previewHint, lblFile, lblSpeed, lblStatus;
        Button btnBrowse, btnColor, btnApply, btnPause, btnStop;
        ComboBox cbMode, cbMonitor;
        TrackBar tbSpeed;
        CheckBox chkPause, chkStartup;
        NotifyIcon tray;
        ToolStripMenuItem miPause, miStop;
        readonly System.Windows.Forms.Timer coverTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly System.Windows.Forms.Timer reattachTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly System.Windows.Forms.Timer watchdogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        string pendingReattachReason = "";
        int watchdogTicks;
        DateTime lastDwmFix = DateTime.MinValue;
        readonly Queue<DateTime> recentReattach = new Queue<DateTime>();

        public MainForm()
        {
            BuildUi();
            BuildTray();
            LoadSettingsIntoUi();

            coverTimer.Tick += delegate { CheckCovered(); };
            coverTimer.Start();
            reattachTimer.Tick += delegate
            {
                reattachTimer.Stop();
                if (wallpaper != null) Reattach(pendingReattachReason);
            };
            watchdogTimer.Tick += delegate { Watchdog(); };
            watchdogTimer.Start();
            quickCheckTimer.Tick += delegate { QuickCheck(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        // ── Reaksi instan: dengarkan Explorer membuat/menghapus/menyusun ulang jendela desktop ──
        IntPtr winEventHook;
        uint hookedPid;
        Native.WinEventProc winEventProc; // disimpan agar tidak di-GC
        readonly System.Windows.Forms.Timer quickCheckTimer = new System.Windows.Forms.Timer { Interval = 350 };

        void EnsureExplorerHook()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            uint pid = 0;
            if (progman != IntPtr.Zero) Native.GetWindowThreadProcessId(progman, out pid);
            if (pid == hookedPid && winEventHook != IntPtr.Zero) return;
            if (winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(winEventHook); winEventHook = IntPtr.Zero; }
            hookedPid = pid;
            if (pid == 0) return;
            if (winEventProc == null) winEventProc = OnWinEvent;
            // EVENT_OBJECT_CREATE (0x8000) .. EVENT_OBJECT_REORDER (0x8004), hanya proses Explorer.
            winEventHook = Native.SetWinEventHook(0x8000, 0x8004, IntPtr.Zero, winEventProc, pid, 0, 0 /*OUTOFCONTEXT*/);
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (wallpaper == null || idObject != 0 /*OBJID_WINDOW*/ || hwnd == IntPtr.Zero) return;
            var sb = new StringBuilder(32);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "WorkerW" || cls == "Progman" || cls == "SHELLDLL_DefView")
            {
                // Tunggu sebentar sampai Explorer selesai merombak, baru periksa (debounce).
                quickCheckTimer.Stop();
                quickCheckTimer.Start();
            }
        }

        void QuickCheck()
        {
            quickCheckTimer.Stop();
            if (wallpaper == null || busy) return;
            string problem = wallpaper.CheckHealth();
            if (problem != null) Reattach(problem + " [instan]");
        }

        // ── Watchdog: pulihkan otomatis kalau Explorer merombak lapisan desktop ──
        void Watchdog()
        {
            if (wallpaper == null || busy) return;
            EnsureExplorerHook();
            watchdogTicks++;
            string problem = wallpaper.CheckHealth();
            // Tiap ~10 detik: pastikan GIF benar-benar terlihat oleh DWM (bukan hitam/kosong).
            if (problem == null && watchdogTicks % 5 == 0 && (DateTime.Now - lastDwmFix).TotalSeconds > 20)
            {
                bool invisible = false;
                try { invisible = wallpaper.InvisibleToDwm(); }
                catch (Exception ex) { Log.Write("Cek DWM gagal: " + ex.Message); }
                if (invisible)
                {
                    lastDwmFix = DateTime.Now;
                    problem = "GIF tidak terlihat (isi jendela kosong/hitam di DWM)";
                }
            }
            if (problem != null) Reattach(problem);
        }

        void ScheduleReattach(string reason)
        {
            if (wallpaper == null) return;
            Log.Write("Event: " + reason);
            pendingReattachReason = reason;
            reattachTimer.Stop();
            reattachTimer.Start();
        }

        // Pasang ulang jendela wallpaper dengan GIF yang sudah dimuat (cepat, tanpa baca file lagi).
        bool Reattach(string reason)
        {
            if (wallpaper == null || busy) return false;

            // Rem darurat: jangan sampai berputar terus kalau ada yang terus-menerus mengganggu.
            DateTime now = DateTime.Now;
            while (recentReattach.Count > 0 && (now - recentReattach.Peek()).TotalSeconds > 60) recentReattach.Dequeue();
            if (recentReattach.Count >= 10)
            {
                Log.Write("Lewati pasang ulang (sudah 10x dalam 1 menit): " + reason);
                return false;
            }
            recentReattach.Enqueue(now);

            Log.Write("Pasang ulang wallpaper — alasan: " + reason);
            WallpaperWindow old = wallpaper;
            old.Paused = true; // hentikan animasi lama supaya player tidak dimajukan dua jendela
            Rectangle[] rects = Array.ConvertAll(TargetScreens(), s => s.Bounds);
            var w = new WallpaperWindow(old.Player, rects, settings.Mode, settings.Background, SpeedFactor, false);
            w.Paused = userPaused || autoPaused;
            string info;
            if (!w.AttachToDesktop(out info))
            {
                Log.Write("Pasang ulang gagal: " + info + " (dicoba lagi oleh watchdog)");
                w.Dispose();
                old.Paused = userPaused || autoPaused;
                return false;
            }
            old.ReleasePlayer();
            w.TakePlayerOwnership();
            wallpaper = w;
            old.Close();
            old.Dispose();
            Log.Write("Pasang ulang OK: " + info);
            return true;
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect ||
                e.Reason == SessionSwitchReason.RemoteConnect)
            {
                string r = "sesi Windows: " + e.Reason;
                if (IsHandleCreated) BeginInvoke(new Action(delegate { ScheduleReattach(r); }));
            }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && IsHandleCreated)
                BeginInvoke(new Action(delegate { ScheduleReattach("bangun dari sleep"); }));
        }

        // ── UI ──
        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "GIF Wallpaper";
            Icon = appIcon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            const int W = 460;
            var root = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16), Location = Point.Empty
            };
            Controls.Add(root);

            // Pratinjau
            previewPanel = new Panel
            {
                Size = new Size(W, 250), BackColor = Color.FromArgb(28, 28, 32),
                Margin = new Padding(0, 0, 0, 10), Cursor = Cursors.Hand
            };
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false, BackColor = previewPanel.BackColor };
            previewHint = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(175, 175, 185),
                Text = "Seret file GIF ke sini\natau klik untuk memilih"
            };
            previewPanel.Controls.Add(preview);
            previewPanel.Controls.Add(previewHint);
            root.Controls.Add(previewPanel);

            // GIF bawaan (folder "gifs" di samping exe)
            galleryTitle = new Label { Text = "GIF bawaan", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4), Visible = false };
            gallery = new FlowLayoutPanel { Width = W, Height = TileH + 8, WrapContents = false, AutoScroll = true, Margin = new Padding(0, 0, 0, 10), Visible = false };
            root.Controls.Add(galleryTitle);
            root.Controls.Add(gallery);
            LoadGallery(); // di sini (sebelum ResumeLayout) supaya ikut di-scale DPI bersama kontrol lain

            // Baris file
            var fileRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblFile = new Label
            {
                Text = "Belum ada file dipilih", AutoEllipsis = true, AutoSize = false, Height = 26,
                Anchor = AnchorStyles.Left | AnchorStyles.Right, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
            };
            btnBrowse = new Button { Text = "Pilih GIF…", AutoSize = true, Padding = new Padding(8, 1, 8, 1), Margin = new Padding(8, 0, 0, 0) };
            fileRow.Controls.Add(lblFile, 0, 0);
            fileRow.Controls.Add(btnBrowse, 1, 0);
            root.Controls.Add(fileRow);

            // Pengaturan
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            cbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, MinimumSize = new Size(250, 0) };
            cbMode.Items.AddRange(new object[] { "Isi layar (dipotong)", "Pas di layar", "Regangkan", "Di tengah", "Ubin (tile)" });
            cbMonitor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, MinimumSize = new Size(250, 0) };
            btnColor = new Button { Width = 110, Height = 26, FlatStyle = FlatStyle.Flat };

            tbSpeed = new TrackBar { Minimum = 1, Maximum = 12, TickFrequency = 1, Width = 250, Margin = new Padding(0) };
            lblSpeed = new Label { AutoSize = true, Margin = new Padding(4, 6, 0, 0) };
            var speedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            speedRow.Controls.Add(tbSpeed);
            speedRow.Controls.Add(lblSpeed);

            AddRow(grid, "Tampilan", cbMode);
            AddRow(grid, "Monitor", cbMonitor);
            AddRow(grid, "Warna latar", btnColor);
            AddRow(grid, "Kecepatan", speedRow);
            root.Controls.Add(grid);

            chkPause = new CheckBox { Text = "Jeda otomatis saat desktop tertutup jendela lain (hemat CPU)", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            chkStartup = new CheckBox { Text = "Jalankan otomatis saat Windows menyala", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            root.Controls.Add(chkPause);
            root.Controls.Add(chkStartup);

            // Tombol aksi
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
            btnApply = new Button { Text = "Pasang sebagai Wallpaper", AutoSize = true, Padding = new Padding(10, 3, 10, 3), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0) };
            btnPause = new Button { Text = "Jeda", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0) };
            btnStop = new Button { Text = "Hentikan", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0) };
            actions.Controls.Add(btnApply);
            actions.Controls.Add(btnPause);
            actions.Controls.Add(btnStop);
            root.Controls.Add(actions);

            lblStatus = new Label { AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Color.FromArgb(90, 90, 100), Margin = new Padding(0, 10, 0, 0) };
            root.Controls.Add(lblStatus);

            // Event
            btnBrowse.Click += delegate { Browse(); };
            previewPanel.Click += delegate { Browse(); };
            preview.Click += delegate { Browse(); };
            previewHint.Click += delegate { Browse(); };
            foreach (Control c in new Control[] { this, previewPanel, preview, previewHint })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnter;
                c.DragDrop += OnDragDrop;
            }
            cbMode.SelectedIndexChanged += delegate
            {
                if (loadingUi) return;
                settings.Mode = (FitMode)cbMode.SelectedIndex; settings.Save();
                if (wallpaper != null) wallpaper.Mode = settings.Mode;
            };
            cbMonitor.SelectedIndexChanged += async delegate
            {
                if (loadingUi) return;
                settings.Monitor = cbMonitor.SelectedIndex - 1; settings.Save();
                if (wallpaper != null) await ApplyWallpaperAsync();
            };
            btnColor.Click += async delegate
            {
                using (var dlg = new ColorDialog { Color = settings.Background, FullOpen = true })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    settings.Background = dlg.Color; settings.Save();
                    UpdateColorButton();
                    if (wallpaper != null) await ApplyWallpaperAsync();
                }
            };
            tbSpeed.ValueChanged += delegate
            {
                UpdateSpeedLabel();
                if (loadingUi) return;
                settings.SpeedStep = tbSpeed.Value; settings.Save();
                if (wallpaper != null) wallpaper.Speed = SpeedFactor;
            };
            chkPause.CheckedChanged += delegate
            {
                if (loadingUi) return;
                settings.PauseWhenCovered = chkPause.Checked; settings.Save();
                CheckCovered();
            };
            chkStartup.CheckedChanged += delegate
            {
                if (loadingUi) return;
                SetStartup(chkStartup.Checked);
            };
            btnApply.Click += async delegate { await ApplyWallpaperAsync(); };
            btnPause.Click += delegate { TogglePause(); };
            btnStop.Click += delegate { StopWallpaper(true); };

            ResumeLayout(false);
            PerformLayout();
        }

        static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 8) };
            control.Anchor = AnchorStyles.Left;
            if (control.Margin == Padding.Empty || control is ComboBox || control is Button)
                control.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("Buka GIF Wallpaper", null, delegate { ShowWindow(); }) { Font = new Font(menu.Font, FontStyle.Bold) };
            miPause = new ToolStripMenuItem("Jeda", null, delegate { TogglePause(); });
            miStop = new ToolStripMenuItem("Hentikan wallpaper", null, delegate { StopWallpaper(true); });
            menu.Items.Add(miOpen);
            menu.Items.Add(miPause);
            menu.Items.Add(miStop);
            menu.Items.Add(new ToolStripMenuItem("Buka log", null, delegate
            {
                try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Keluar", null, delegate { ExitApp(); }));
            tray = new NotifyIcon { Icon = appIcon, Text = "GIF Wallpaper", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        void LoadSettingsIntoUi()
        {
            loadingUi = true;
            cbMode.SelectedIndex = (int)settings.Mode;
            RefreshMonitorList();
            tbSpeed.Value = settings.SpeedStep;
            UpdateSpeedLabel();
            UpdateColorButton();
            chkPause.Checked = settings.PauseWhenCovered;
            chkStartup.Checked = IsStartupEnabled();
            loadingUi = false;
            if (File.Exists(settings.GifPath)) LoadPreview(settings.GifPath);
            else if (galleryTiles.Count > 0) SetGif((string)galleryTiles[0].Tag); // pertama kali: pakai GIF bawaan pertama
            UpdateUi();
            UpdateStatus();
        }

        // ── Galeri GIF bawaan ──
        const int ThumbW = 144, ThumbH = 81, TileW = ThumbW + 4, TileH = ThumbH + 26;
        Label galleryTitle;
        FlowLayoutPanel gallery;
        readonly List<Panel> galleryTiles = new List<Panel>();
        readonly ToolTip galleryTips = new ToolTip();
        static readonly Color Accent = Color.FromArgb(124, 77, 255);

        static string GalleryDir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gifs"); } }

        void LoadGallery()
        {
            string[] files;
            try { files = Directory.Exists(GalleryDir) ? Directory.GetFiles(GalleryDir, "*.gif") : new string[0]; }
            catch { files = new string[0]; }
            Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);
            if (files.Length == 0) return;

            foreach (string f in files)
            {
                string path = f;
                var tile = new Panel { Size = new Size(TileW, TileH), Margin = new Padding(0, 0, 6, 0), Padding = new Padding(2), Cursor = Cursors.Hand, Tag = path };
                // Dock (bukan posisi absolut) → gambar & label selalu mengikuti ukuran tile setelah scaling DPI.
                var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 32), Cursor = Cursors.Hand };
                var lbl = new Label
                {
                    Dock = DockStyle.Bottom, Height = 22, Text = Path.GetFileNameWithoutExtension(path),
                    TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Cursor = Cursors.Hand
                };
                tile.Controls.Add(pic);   // Controls[0] — Fill di-layout terakhir (urutan dock terbalik)
                tile.Controls.Add(lbl);   // Controls[1] — Bottom di-layout duluan
                foreach (Control c in new Control[] { tile, pic, lbl })
                {
                    c.Click += delegate { if (!busy) SetGif(path); };
                    galleryTips.SetToolTip(c, lbl.Text);
                }
                galleryTiles.Add(tile);
                gallery.Controls.Add(tile);
            }
            galleryTiles[galleryTiles.Count - 1].Margin = Padding.Empty; // tanpa jarak ekstra di ujung → tidak memicu scrollbar
            if (files.Length * (TileW + 6) - 6 > gallery.Width) gallery.Height += SystemInformation.HorizontalScrollBarHeight;
            galleryTitle.Visible = gallery.Visible = true;
            UpdateGallerySelection();
        }

        // Thumbnail dibuat saat jendela pertama kali tampil (bukan saat start di tray),
        // di thread latar (decode GIF besar butuh beberapa ratus ms), lalu di-cache ke disk.
        bool thumbsStarted;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) StartThumbs();
        }

        void StartThumbs()
        {
            if (thumbsStarted || galleryTiles.Count == 0 || !IsHandleCreated) return;
            thumbsStarted = true;
            var tiles = galleryTiles.ToArray();
            Task.Run(() =>
            {
                foreach (Panel t in tiles)
                {
                    Image thumb = GalleryThumb.Get((string)t.Tag, ThumbW * 2, ThumbH * 2);
                    if (thumb == null) continue;
                    try { BeginInvoke(new Action(() => ((PictureBox)t.Controls[0]).Image = thumb)); }
                    catch { thumb.Dispose(); return; }
                }
                BeginInvoke(new Action(ReleaseGarbage));
            });
        }

        void UpdateGallerySelection()
        {
            foreach (Panel t in galleryTiles)
            {
                bool sel = InstallInfo.SamePath((string)t.Tag, settings.GifPath);
                t.BackColor = sel ? Accent : Color.Transparent;
                t.Controls[1].ForeColor = sel ? Color.White : ForeColor;
            }
        }

        void RefreshMonitorList()
        {
            bool prev = loadingUi;
            loadingUi = true;
            cbMonitor.Items.Clear();
            cbMonitor.Items.Add("Semua monitor");
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                cbMonitor.Items.Add(string.Format("Monitor {0}: {1}×{2}{3}", i + 1, all[i].Bounds.Width, all[i].Bounds.Height,
                    all[i].Primary ? " (utama)" : ""));
            int sel = settings.Monitor + 1;
            cbMonitor.SelectedIndex = sel >= 0 && sel < cbMonitor.Items.Count ? sel : 0;
            loadingUi = prev;
        }

        double SpeedFactor { get { return settings.SpeedStep * 0.25; } }

        void UpdateSpeedLabel() { lblSpeed.Text = (tbSpeed.Value * 0.25).ToString("0.##") + "×"; }

        void UpdateColorButton()
        {
            Color c = settings.Background;
            btnColor.BackColor = c;
            btnColor.ForeColor = (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 140 ? Color.Black : Color.White;
            btnColor.Text = ColorTranslator.ToHtml(Color.FromArgb(c.R, c.G, c.B));
        }

        void UpdateUi()
        {
            bool active = wallpaper != null;
            btnApply.Enabled = !busy && File.Exists(settings.GifPath);
            btnApply.Text = busy ? "Memuat…" : "Pasang sebagai Wallpaper";
            btnPause.Enabled = active && !busy;
            btnPause.Text = userPaused ? "Lanjutkan" : "Jeda";
            btnStop.Enabled = active && !busy;
            miPause.Enabled = active;
            miPause.Text = userPaused ? "Lanjutkan" : "Jeda";
            miStop.Enabled = active;
        }

        void SetStatus(string text)
        {
            lblStatus.Text = text;
            string tip = "GIF Wallpaper — " + text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "…" : tip;
        }

        void UpdateStatus()
        {
            if (busy) return;
            if (wallpaper == null) SetStatus("Wallpaper tidak aktif.");
            else if (userPaused) SetStatus("Dijeda.");
            else if (autoPaused) SetStatus("Dijeda otomatis — desktop sedang tertutup jendela lain.");
            else SetStatus("Aktif: " + Path.GetFileName(settings.GifPath));
        }

        // ── File ──
        void Browse()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Pilih GIF",
                Filter = "GIF animasi (*.gif)|*.gif|Semua gambar|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            })
            {
                if (File.Exists(settings.GifPath)) dlg.InitialDirectory = Path.GetDirectoryName(settings.GifPath);
                if (dlg.ShowDialog(this) == DialogResult.OK) SetGif(dlg.FileName);
            }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) SetGif(files[0]);
        }

        void SetGif(string path)
        {
            if (!LoadPreview(path)) return;
            settings.GifPath = path;
            settings.Save();
            UpdateGallerySelection();
            UpdateUi();
            SetStatus(wallpaper != null
                ? "GIF baru dipilih. Klik “Pasang sebagai Wallpaper” untuk menerapkannya."
                : "Siap. Klik “Pasang sebagai Wallpaper”.");
        }

        // Buffer file GIF (puluhan MB) tinggal di heap sampai GC berikutnya, yang bisa lama sekali
        // pada app yang hampir diam → bersihkan sekarang supaya RAM langsung turun.
        static void ReleaseGarbage()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }

        bool LoadPreview(string path)
        {
            try
            {
                var ms = new MemoryStream(File.ReadAllBytes(path));
                Image img = Image.FromStream(ms);
                int w = img.Width, h = img.Height;
                if (ms.Length > 8 * 1024 * 1024)
                {
                    // GIF besar: preview animasi GDI+ memakan RAM & CPU → cukup thumbnail frame pertama.
                    double k = Math.Min(1.0, Math.Min(640.0 / w, 360.0 / h));
                    Image thumb = new Bitmap(img, Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)));
                    img.Dispose(); ms.Dispose(); ms = null;
                    img = thumb;
                }
                Image oldImg = preview.Image;
                preview.Image = img;
                if (oldImg != null) oldImg.Dispose();
                if (previewStream != null) previewStream.Dispose();
                previewStream = ms;
                preview.Visible = true;
                previewHint.Visible = false;
                lblFile.Text = Path.GetFileName(path) + "  ·  " + w + "×" + h;
                ReleaseGarbage();
                return true;
            }
            catch (Exception)
            {
                SetStatus("File itu tidak bisa dibuka sebagai gambar: " + Path.GetFileName(path));
                return false;
            }
        }

        // ── Wallpaper ──
        Screen[] TargetScreens()
        {
            Screen[] all = Screen.AllScreens;
            if (settings.Monitor >= 0 && settings.Monitor < all.Length) return new[] { all[settings.Monitor] };
            return all;
        }

        async Task<bool> ApplyWallpaperAsync()
        {
            if (busy) return false;
            string path = settings.GifPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus("Pilih file GIF dulu.");
                return false;
            }

            busy = true;
            UpdateUi();
            SetStatus("Memuat GIF…");
            Color bg = settings.Background;
            GifPlayer player;
            try
            {
                player = await Task.Run(() => new GifPlayer(path, bg));
            }
            catch (Exception ex)
            {
                busy = false;
                UpdateUi();
                SetStatus("Gagal memuat GIF: " + ex.Message);
                return false;
            }

            Log.Write(string.Format("GIF dimuat: {0} — {1}×{2}, {3} frame, {4}", player.Name, player.Width, player.Height,
                player.FrameCount, player.AllFramesCached ? "semua frame di-cache" : "mode streaming (decode di thread latar)"));
            ReleaseGarbage();
            Rectangle[] rects = Array.ConvertAll(TargetScreens(), s => s.Bounds);
            var w = new WallpaperWindow(player, rects, settings.Mode, bg, SpeedFactor, true);
            string info;
            if (!w.AttachToDesktop(out info))
            {
                Log.Write("Gagal memasang: " + info);
                w.Dispose();
                busy = false;
                UpdateUi();
                SetStatus("Gagal memasang ke desktop: " + info + ".");
                return false;
            }
            Log.Write("Wallpaper terpasang: " + info);

            // Jendela baru sudah tampil di atas yang lama → tutup yang lama tanpa kedip.
            WallpaperWindow old = wallpaper;
            wallpaper = w;
            if (old != null) { old.Close(); old.Dispose(); }

            userPaused = false;
            autoPaused = false;
            settings.Active = true;
            settings.Save();
            busy = false;
            UpdateUi();
            UpdateStatus();
            CheckCovered();
            return true;
        }

        void StopWallpaper(bool userAction)
        {
            if (wallpaper != null)
            {
                Log.Write(userAction ? "Wallpaper dihentikan oleh pengguna" : "Wallpaper dilepas (app keluar)");
                wallpaper.Close();
                wallpaper.Dispose();
                wallpaper = null;
                DesktopHost.RefreshWallpaper();
            }
            userPaused = false;
            autoPaused = false;
            if (userAction)
            {
                settings.Active = false;
                settings.Save();
            }
            UpdateUi();
            UpdateStatus();
        }

        void TogglePause()
        {
            if (wallpaper == null) return;
            userPaused = !userPaused;
            wallpaper.Paused = userPaused || autoPaused;
            UpdateUi();
            UpdateStatus();
        }

        void CheckCovered()
        {
            if (wallpaper == null) return;
            bool covered = settings.PauseWhenCovered && IsDesktopCovered();
            if (covered == autoPaused) return;
            autoPaused = covered;
            wallpaper.Paused = userPaused || autoPaused;
            UpdateStatus();
        }

        bool IsDesktopCovered()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            if (!Native.IsWindowVisible(fg) || Native.IsIconic(fg)) return false;
            var sb = new StringBuilder(256);
            Native.GetClassName(fg, sb, sb.Capacity);
            switch (sb.ToString())
            {
                case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
                case "NotifyIconOverflowWindow": case "Windows.UI.Core.CoreWindow": case "XamlExplorerHostIslandWindow":
                    return false;
            }
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            Rectangle wr = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            foreach (Screen s in TargetScreens())
                if (!wr.Contains(s.WorkingArea)) return false;
            return true;
        }

        // ── Startup & tray ──
        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Tidak bisa mengubah pengaturan startup: " + ex.Message);
            }
        }

        public void StartInTray()
        {
            IntPtr h = Handle; // buat handle tanpa menampilkan jendela
            BeginInvoke(new Action(AutoStart));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AutoStart();
        }

        async void AutoStart()
        {
            if (autoStarted) return;
            autoStarted = true;
            if (!settings.Active || !File.Exists(settings.GifPath)) return;
            // Saat login, Explorer kadang belum siap → coba beberapa kali.
            for (int i = 0; i < 15; i++)
            {
                if (await ApplyWallpaperAsync()) return;
                await Task.Delay(2000);
            }
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            reallyExit = true;
            coverTimer.Stop();
            reattachTimer.Stop();
            watchdogTimer.Stop();
            StopWallpaper(false); // tetap ingat "aktif" supaya dipasang lagi saat app dibuka berikutnya
            tray.Visible = false;
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                if (wallpaper != null)
                {
                    e.Cancel = true;
                    Hide();
                    if (!trayHintShown)
                    {
                        trayHintShown = true;
                        tray.ShowBalloonTip(3000, "GIF Wallpaper masih berjalan",
                            "Wallpaper tetap aktif. Klik kanan ikon di tray untuk membuka atau keluar.", ToolTipIcon.Info);
                    }
                    return;
                }
                BeginInvoke(new Action(ExitApp));
                e.Cancel = true;
                return;
            }
            if (!reallyExit) // shutdown / logoff
            {
                reallyExit = true;
                StopWallpaper(false);
                tray.Visible = false;
            }
            base.OnFormClosing(e);
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                RefreshMonitorList();
                ScheduleReattach("pengaturan layar berubah");
            }));
        }

        protected override void WndProc(ref Message m)
        {
            // Explorer di-restart → jendela wallpaper ikut hancur, pasang ulang.
            if (m.Msg == taskbarCreatedMsg && taskbarCreatedMsg != 0 && wallpaper != null)
                ScheduleReattach("Explorer di-restart (TaskbarCreated)");
            // Installer/uninstaller minta keluar → lepas wallpaper dengan rapi.
            if (m.Msg == exitMsg && exitMsg != 0)
            {
                Log.Write("Diminta keluar oleh installer/uninstaller");
                BeginInvoke(new Action(ExitApp));
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (winEventHook != IntPtr.Zero) Native.UnhookWinEvent(winEventHook);
                quickCheckTimer.Dispose();
                watchdogTimer.Dispose();
                coverTimer.Dispose();
                reattachTimer.Dispose();
                if (tray != null) tray.Dispose();
                if (wallpaper != null) wallpaper.Dispose();
            }
            base.Dispose(disposing);
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = new GraphicsPath())
                {
                    var r = new Rectangle(1, 1, 29, 29);
                    int d = 12;
                    path.AddArc(r.X, r.Y, d, d, 180, 90);
                    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                        Color.FromArgb(124, 77, 255), Color.FromArgb(0, 176, 255), 45f))
                        g.FillPath(br, path);
                }
                g.FillPolygon(Brushes.White, new[] { new Point(12, 9), new Point(12, 23), new Point(23, 16) });
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // ───────────────────────── Thumbnail galeri ─────────────────────────
    // Satu frame (sepertiga durasi, biasanya lebih menarik dari frame pertama), di-cache sebagai PNG
    // di %APPDATA%\GifWallpaper\thumbs supaya galeri berikutnya langsung tampil.
    static class GalleryThumb
    {
        static string CacheDir { get { return Path.Combine(Log.Dir, "thumbs"); } }

        public static unsafe Image Get(string path, int w, int h)
        {
            try
            {
                var fi = new FileInfo(path);
                string name = Path.GetFileNameWithoutExtension(path);
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                string cache = Path.Combine(CacheDir, name + "_" + fi.Length + "_" + fi.LastWriteTimeUtc.Ticks + "_" + w + ".png");
                if (File.Exists(cache)) return new Bitmap(new MemoryStream(File.ReadAllBytes(cache)));

                byte[] file = File.ReadAllBytes(path);
                if (!GifDecoder.IsGif(file)) return null;
                using (var dec = new GifDecoder(file, 0x1C1C20))
                {
                    file = null;
                    int target = Math.Min(dec.FrameCount / 3, 90);
                    for (int i = 0; i <= target; i++) dec.DecodeNext();
                    var thumb = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                    using (var src = new Bitmap(dec.Width, dec.Height, dec.Width * 4, PixelFormat.Format32bppRgb, (IntPtr)dec.Canvas))
                    using (Graphics g = Graphics.FromImage(thumb))
                    {
                        g.Clear(Color.FromArgb(28, 28, 32));
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float s = Math.Max(w / (float)dec.Width, h / (float)dec.Height); // isi penuh, potong pinggir
                        float dw = dec.Width * s, dh = dec.Height * s;
                        g.DrawImage(src, (w - dw) / 2f, (h - dh) / 2f, dw, dh);
                    }
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        thumb.Save(cache, ImageFormat.Png);
                    }
                    catch { }
                    return thumb;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Thumbnail gagal untuk " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }
    }

    // ───────────────────────── Uninstall ─────────────────────────
    // Dipanggil lewat "GifWallpaper.exe --uninstall" (dari Settings › Apps / Control Panel).
    static class Uninstaller
    {
        public static void Run(bool quiet)
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            bool installedCopy = InstallInfo.SamePath(InstallInfo.InstalledDir(), dir);

            if (!quiet && MessageBox.Show(
                    "Hapus GIF Wallpaper dari komputer ini?\n\nWallpaper animasi akan dihentikan dan wallpaper Windows biasa tampil kembali.",
                    "Hapus GIF Wallpaper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            InstallInfo.CloseRunningInstances();
            try
            {
                string cmd = InstallInfo.StartupCommand();
                if (cmd != null && cmd.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0) InstallInfo.SetStartup(exe, false);
            }
            catch { }
            foreach (string lnk in new[] { InstallInfo.StartMenuShortcut, InstallInfo.DesktopShortcut })
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }
            if (installedCopy)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKey, false); } catch { }
            }

            if (!quiet && Directory.Exists(Log.Dir) && MessageBox.Show(
                    "Hapus juga pengaturan dan log GIF Wallpaper?\n\nPilih \"No\" kalau mungkin akan menginstal lagi nanti.",
                    "Hapus GIF Wallpaper", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                try { Directory.Delete(Log.Dir, true); } catch { }
            }

            if (!quiet)
                MessageBox.Show("GIF Wallpaper sudah dihapus dari komputer ini.", "Hapus GIF Wallpaper",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Folder instalasi baru bisa dihapus setelah exe ini keluar. Hanya untuk folder yang
            // tercatat di registry sebagai lokasi instalasi — salinan di tempat lain tidak disentuh.
            if (installedCopy && dir.Length > 3)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + dir + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
                    });
                }
                catch { }
            }
        }
    }

    // ───────────────────────── Win32 ─────────────────────────
    static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint LWA_ALPHA = 0x2;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const int HALFTONE = 4;
        public const uint SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int val);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, uint count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pv, uint winIni);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern int SaveDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool RestoreDC(IntPtr hdc, int saved);
        [DllImport("gdi32.dll")] public static extern int ExcludeClipRect(IntPtr hdc, int l, int t, int r, int b);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    }
}
