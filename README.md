# GIF Wallpaper

Pasang GIF animasi sebagai **live wallpaper** Windows: tampil di belakang ikon desktop, ringan, dan tanpa dependensi tambahan.

## Unduh

Ambil **`GIF-Wallpaper-Setup.exe`** dari halaman [Releases](../../releases/latest), lalu jalankan.

- Tidak perlu izin administrator. Aplikasi diinstal per pengguna ke `%LOCALAPPDATA%\Programs\GIF Wallpaper`.
- Sudah termasuk 3 GIF bawaan: **Kelopak Sakura**, **Perpustakaan Ungu**, **Tatapan Merah**.
- Bisa di-uninstall dari **Settings → Apps**.

> Installer ini belum ditandatangani secara digital, jadi Windows SmartScreen mungkin menampilkan *"Windows protected your PC"*. Klik **More info → Run anyway**.

## Fitur

- **Di belakang ikon desktop.** Mendukung Windows 10 dan Windows 11 (termasuk desktop model baru di 24H2+).
- **Hemat resource.**
  - Decoder GIF sendiri, 4–5× lebih cepat dari GDI+, berjalan di thread latar.
  - GIF besar di-*stream*, jadi RAM hanya sekitar ukuran file.
  - Jeda otomatis saat desktop tertutup jendela lain.
- **Tampilan:** isi layar, pas, regangkan, tengah, atau ubin.
- **Pengaturan lain:** pilih monitor, warna latar, dan kecepatan 0,25×–3×.
- **Galeri GIF bawaan.** Taruh file `.gif` di folder `gifs` di samping exe, dan GIF itu otomatis muncul di galeri.
- **Tangguh.** Aplikasi memasang ulang wallpaper secara otomatis saat:
  - Explorer di-restart,
  - slideshow Windows berganti gambar,
  - layar berubah, atau
  - komputer bangun dari sleep.
- **Bisa jalan otomatis saat Windows menyala** dan berjalan dari system tray.

## Build sendiri

Tidak perlu Visual Studio. Cukup compiler C# bawaan Windows (.NET Framework 4.x):

```bat
build.cmd
```

Hasil build:

- `GifWallpaper.exe`: aplikasinya.
- `dist\GIF-Wallpaper-Setup.exe`: installer berisi aplikasi beserta semua GIF di folder `gifs\`.

## Struktur

| File | Isi |
|---|---|
| `GifWallpaper.cs` | Aplikasi: UI, tray, penempelan ke desktop, watchdog, render |
| `GifDecoder.cs` | Decoder GIF (LZW, disposal, interlace) |
| `Shared.cs` | Kode bersama aplikasi & installer (registry, shortcut) |
| `Setup.cs` | Installer |
| `app.manifest`, `app.ico` | Manifest (DPI, kompatibilitas Windows) & ikon |
| `gifs/` | GIF bawaan yang ikut di installer |

## Opsi baris perintah

```
GifWallpaper.exe --tray                  mulai di system tray
GifWallpaper.exe --uninstall [--quiet]   hapus instalasi
GIF-Wallpaper-Setup.exe /S [/D=folder] [/nodesktop] [/nostartup] [/norun]   instal tanpa dialog
```

Untuk diagnostik, set `GIFWALLPAPER_STATS=1` supaya fps dicatat ke `%APPDATA%\GifWallpaper\log.txt`.
