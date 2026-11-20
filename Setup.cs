// Installer GIF Wallpaper: satu file Setup.exe berisi GifWallpaper.exe + GIF bawaan (embedded resource).
// Instal per pengguna (tanpa admin) ke %LOCALAPPDATA%\Programs\GIF Wallpaper, plus shortcut Start Menu,
// entri "Apps & features" (uninstall), dan opsi startup.
//
// Mode senyap untuk otomatisasi:  Setup.exe /S [/D=folder] [/nodesktop] [/nostartup] [/norun]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GifWallpaper
{
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            var opt = new SetupOptions();
            bool silent = false;
            foreach (string a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/s" || l == "/silent") silent = true;
                else if (l.StartsWith("/d=")) opt.Dir = a.Substring(3).Trim('"');
                else if (l == "/nodesktop") opt.Desktop = false;
                else if (l == "/nostartup") opt.Startup = false;
                else if (l == "/norun") opt.Launch = false;
            }
            if (silent)
            {
                try
                {
                    Installer.Install(opt, null);
                    if (opt.Launch) Process.Start(Path.Combine(Path.GetFullPath(opt.Dir), InstallInfo.ExeName), "--tray");
                    return 0;
                }
                catch { return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(opt));
            return 0;
        }

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    }

    class SetupOptions
    {
        public string Dir = InstallInfo.InstalledDir() ?? InstallInfo.DefaultInstallDir;
        public bool Desktop = true, Startup = true, Launch = true;
    }

    static class Installer
    {
        const string Prefix = "payload/";

        public static List<string> Payload()
        {
            var list = new List<string>();
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(Prefix)) list.Add(n);
            return list;
        }

        public static List<string> BundledGifNames()
        {
            var names = new List<string>();
            foreach (string n in Payload())
                if (n.StartsWith(Prefix + "gifs/", StringComparison.OrdinalIgnoreCase))
                    names.Add(Path.GetFileNameWithoutExtension(n.Substring((Prefix + "gifs/").Length)));
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        public static long PayloadBytes()
        {
            long total = 0;
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in Payload())
                using (Stream s = asm.GetManifestResourceStream(n)) total += s.Length;
            return total;
        }

        // progress(persen 0..100, teks). Melempar exception bila gagal.
        public static void Install(SetupOptions opt, Action<int, string> progress)
        {
            if (progress == null) progress = delegate { };
            string dir = Path.GetFullPath(opt.Dir);
            string exe = Path.Combine(dir, InstallInfo.ExeName);

            progress(0, "Menutup GIF Wallpaper yang sedang berjalan…");
            InstallInfo.CloseRunningInstances();

            Assembly asm = Assembly.GetExecutingAssembly();
            List<string> files = Payload();
            long total = Math.Max(1, PayloadBytes()), done = 0;
            var buf = new byte[1 << 20];
            foreach (string res in files)
            {
                string rel = res.Substring(Prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                string target = Path.Combine(dir, rel);
                progress((int)(done * 90 / total), "Menyalin " + Path.GetFileName(rel) + "…");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream src = asm.GetManifestResourceStream(res))
                using (FileStream dst = CreateWithRetry(target))
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        done += n;
                        progress((int)(done * 90 / total), "Menyalin " + Path.GetFileName(rel) + "…");
                    }
                }
            }

            progress(92, "Mendaftarkan aplikasi…");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InstallInfo.UninstallKey))
            {
                k.SetValue("DisplayName", InstallInfo.AppName);
                k.SetValue("DisplayVersion", InstallInfo.Version);
                k.SetValue("Publisher", InstallInfo.AppName);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(total / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            progress(95, "Membuat shortcut…");
            InstallInfo.CreateShortcut(InstallInfo.StartMenuShortcut, exe, "", "Pasang GIF animasi sebagai wallpaper desktop");
            if (opt.Desktop) InstallInfo.CreateShortcut(InstallInfo.DesktopShortcut, exe, "", "Pasang GIF animasi sebagai wallpaper desktop");
            else if (File.Exists(InstallInfo.DesktopShortcut)) File.Delete(InstallInfo.DesktopShortcut);

            InstallInfo.SetStartup(exe, opt.Startup);
            progress(100, "Instalasi selesai.");
        }

        // File lama bisa masih terkunci sebentar setelah prosesnya ditutup.
        static FileStream CreateWithRetry(string path)
        {
            for (int i = 0; ; i++)
            {
                try { return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); }
                catch (IOException)
                {
                    if (i >= 20) throw;
                    System.Threading.Thread.Sleep(250);
                }
            }
        }
    }

    class SetupForm : Form
    {
        readonly SetupOptions opt;
        readonly string previousDir = InstallInfo.InstalledDir();
        TextBox txtDir;
        Button btnBrowse, btnInstall, btnCancel;
        CheckBox chkDesktop, chkStartup, chkLaunch;
        ProgressBar progress;
        Label lblStatus;
        bool installing, finished;

        static readonly Color Purple = Color.FromArgb(124, 77, 255), Blue = Color.FromArgb(0, 176, 255);

        public SetupForm(SetupOptions opt)
        {
            this.opt = opt;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Instal " + InstallInfo.AppName;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 400);
            BackColor = Color.White;

            // Header bergradien
            var header = new Panel { Dock = DockStyle.Top, Height = 88 };
            header.Paint += (s, e) =>
            {
                using (var br = new LinearGradientBrush(header.ClientRectangle, Purple, Blue, 0f))
                    e.Graphics.FillRectangle(br, header.ClientRectangle);
                if (Icon != null)
                    using (var big = new Icon(Icon, 48, 48)) e.Graphics.DrawIcon(big, new Rectangle(20, (header.Height - 48) / 2, 48, 48));
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var f1 = new Font("Segoe UI Semibold", 16F))
                using (var f2 = new Font("Segoe UI", 9.5F))
                {
                    TextRenderer.DrawText(e.Graphics, InstallInfo.AppName, f1, new Point(80, 18), Color.White);
                    TextRenderer.DrawText(e.Graphics, "Live wallpaper GIF untuk Windows  ·  versi " + InstallInfo.Version, f2,
                        new Point(82, 52), Color.FromArgb(235, 235, 255));
                }
            };
            header.Resize += delegate { header.Invalidate(); };

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 12), ColumnCount = 1 };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            List<string> gifs = Installer.BundledGifNames();
            long mb = (Installer.PayloadBytes() + (1 << 20) - 1) >> 20;
            string intro = previousDir != null
                ? "Versi sebelumnya ditemukan dan akan diperbarui. Pengaturan Anda tetap tersimpan."
                : "Diinstal untuk akun Windows Anda saja, tanpa perlu izin administrator.";
            if (gifs.Count > 0) intro += "\n\nTermasuk " + gifs.Count + " GIF bawaan: " + string.Join(", ", gifs) + ".";
            var lblIntro = new Label { Text = intro, AutoSize = true, MaximumSize = new Size(456, 0), Margin = new Padding(0, 0, 0, 14) };

            var lblDir = new Label { Text = "Folder instalasi  (butuh ±" + mb + " MB)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            var dirRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12) };
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            txtDir = new TextBox { Text = opt.Dir, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 6, 0) };
            btnBrowse = new Button { Text = "Ubah…", AutoSize = true, Margin = new Padding(0) };
            dirRow.Controls.Add(txtDir, 0, 0);
            dirRow.Controls.Add(btnBrowse, 1, 0);

            chkDesktop = new CheckBox { Text = "Buat shortcut di Desktop", Checked = opt.Desktop, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            chkStartup = new CheckBox { Text = "Jalankan otomatis saat Windows menyala", Checked = opt.Startup, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            chkLaunch = new CheckBox { Text = "Buka GIF Wallpaper setelah instalasi", Checked = opt.Launch, AutoSize = true, Margin = new Padding(0, 2, 0, 14) };

            progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Visible = false, Margin = new Padding(0, 0, 0, 4) };
            lblStatus = new Label { AutoSize = true, MaximumSize = new Size(456, 0), ForeColor = Color.FromArgb(90, 90, 100), Margin = new Padding(0) };

            foreach (Control c in new Control[] { lblIntro, lblDir, dirRow, chkDesktop, chkStartup, chkLaunch, progress, lblStatus })
                body.Controls.Add(c);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 54,
                Padding = new Padding(16, 10, 16, 10), BackColor = Color.FromArgb(245, 245, 248)
            };
            btnCancel = new Button { Text = "Batal", Size = new Size(96, 30), Margin = new Padding(8, 0, 0, 0) };
            btnInstall = new Button
            {
                Text = previousDir != null ? "Perbarui" : "Instal", Size = new Size(110, 30), Margin = new Padding(0),
                FlatStyle = FlatStyle.Flat, BackColor = Purple, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold)
            };
            btnInstall.FlatAppearance.BorderSize = 0;
            footer.Controls.Add(btnCancel);
            footer.Controls.Add(btnInstall);

            Controls.Add(body);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = btnInstall;
            CancelButton = btnCancel;
            ActiveControl = btnInstall;

            btnBrowse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = "Pilih folder induk untuk " + InstallInfo.AppName, ShowNewFolderButton = true })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        txtDir.Text = Path.Combine(dlg.SelectedPath, InstallInfo.AppName);
                }
            };
            btnCancel.Click += delegate { Close(); };
            btnInstall.Click += async delegate
            {
                if (finished) { Finish(); return; }
                await DoInstall();
            };
            FormClosing += (s, e) => { if (installing) e.Cancel = true; };

            ResumeLayout(false);
            PerformLayout();
        }

        async Task DoInstall()
        {
            string dir = txtDir.Text.Trim();
            try
            {
                if (dir.Length < 4 || !Path.IsPathRooted(dir)) throw new ArgumentException();
                dir = Path.GetFullPath(dir);
            }
            catch
            {
                MessageBox.Show(this, "Folder instalasi tidak valid.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            opt.Dir = dir;
            opt.Desktop = chkDesktop.Checked;
            opt.Startup = chkStartup.Checked;
            opt.Launch = chkLaunch.Checked;

            installing = true;
            foreach (Control c in new Control[] { txtDir, btnBrowse, chkDesktop, chkStartup, chkLaunch, btnInstall, btnCancel }) c.Enabled = false;
            progress.Visible = true;
            try
            {
                await Task.Run(() => Installer.Install(opt, (p, msg) => BeginInvoke(new Action(() =>
                {
                    progress.Value = Math.Max(0, Math.Min(100, p));
                    lblStatus.Text = msg;
                }))));
                finished = true;
                lblStatus.Text = "Instalasi selesai. " + InstallInfo.AppName + " ada di Start Menu" +
                                 (opt.Desktop ? " dan Desktop." : ".");
                btnInstall.Text = "Selesai";
                btnInstall.Enabled = true;
                btnInstall.Focus();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Instalasi gagal: " + ex.Message;
                lblStatus.ForeColor = Color.FromArgb(200, 40, 40);
                foreach (Control c in new Control[] { txtDir, btnBrowse, chkDesktop, chkStartup, chkLaunch, btnInstall, btnCancel }) c.Enabled = true;
            }
            finally { installing = false; }
        }

        void Finish()
        {
            if (opt.Launch)
            {
                try { Process.Start(Path.Combine(opt.Dir, InstallInfo.ExeName)); } catch { }
            }
            Close();
        }
    }
}
