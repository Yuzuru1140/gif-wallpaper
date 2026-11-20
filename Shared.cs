// Kode bersama untuk GifWallpaper.exe (uninstall) dan Setup.exe (instal).

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GifWallpaper
{
    static class InstallInfo
    {
        public const string AppName = "GIF Wallpaper";
        public const string Version = "1.1.0";
        public const string ExeName = "GifWallpaper.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GifWallpaper";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "GifWallpaper";
        public const string ExitMessage = "GifWallpaper.Exit"; // pesan broadcast: "tolong keluar dengan rapi"

        public static string DefaultInstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
        }

        public static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }

        public static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }

        // Lokasi instalasi yang tercatat di registry (null = belum terinstal).
        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k == null ? null : k.GetValue("InstallLocation") as string;
            }
            catch { return null; }
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Minta semua GifWallpaper.exe lain keluar dengan rapi (wallpaper dilepas), lalu paksa bila perlu.
        public static void CloseRunningInstances()
        {
            int msg = RegisterWindowMessage(ExitMessage);
            if (msg != 0) PostMessage(new IntPtr(0xFFFF) /*HWND_BROADCAST*/, msg, IntPtr.Zero, IntPtr.Zero);
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (p.Id == self) continue;
                    if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        public static void CreateShortcut(string lnkPath, string target, string args, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                try
                {
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                    lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args ?? "" });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                }
                finally { Marshal.FinalReleaseComObject(lnk); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static void SetStartup(string exePath, bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + exePath + "\" --tray");
                else k.DeleteValue(RunValue, false);
            }
        }

        public static string StartupCommand()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k == null ? null : k.GetValue(RunValue) as string;
            }
            catch { return null; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
