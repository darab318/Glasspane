using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glasspane.Core;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Native
{
    /// <summary>The part of the wallpaper sitting behind a window, ready to be blurred.</summary>
    internal sealed class WallpaperSlice
    {
        public BitmapSource? Image { get; init; }

        /// <summary>Which part of <see cref="Image"/> is behind the window (0–1 units, may extend past the edges).</summary>
        public Rect Viewbox { get; init; }

        /// <summary>Wallpaper background colour (shows around "Fit" or "Center" wallpapers, or a solid-colour desktop).</summary>
        public Color Background { get; init; }
    }

    /// <summary>
    /// Works out exactly which part of the wallpaper is behind a desktop widget, using the same
    /// interface the Settings app uses for wallpapers. That lets the widget blur its own copy of
    /// the wallpaper by any amount (Windows' built-in blur only has one strength).
    /// </summary>
    internal static class Wallpaper
    {
        private sealed class Monitor
        {
            public RECT Rect;
            public string? Path;
        }

        private static List<Monitor>? _monitors;
        private static int _position;
        private static Color _background = Colors.Black;
        private static readonly Dictionary<string, (DateTime stamp, BitmapSource image)> Images = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget cached wallpaper details, e.g. after the wallpaper or screens changed.</summary>
        public static void Invalidate()
        {
            _monitors = null;
        }

        /// <param name="area">Screen area in physical pixels.</param>
        public static WallpaperSlice? GetSlice(RECT area)
        {
            try
            {
                if (_monitors == null) Load();
                if (_monitors == null || _monitors.Count == 0) return null;

                // The monitor holding the middle of the area
                int cx = (area.Left + area.Right) / 2, cy = (area.Top + area.Bottom) / 2;
                var monitor = _monitors.Find(m => cx >= m.Rect.Left && cx < m.Rect.Right && cy >= m.Rect.Top && cy < m.Rect.Bottom)
                              ?? _monitors[0];

                RECT target = monitor.Rect;
                if (_position == DWPOS_SPAN)
                {
                    target = _monitors[0].Rect;
                    foreach (var m in _monitors)
                    {
                        target.Left = Math.Min(target.Left, m.Rect.Left);
                        target.Top = Math.Min(target.Top, m.Rect.Top);
                        target.Right = Math.Max(target.Right, m.Rect.Right);
                        target.Bottom = Math.Max(target.Bottom, m.Rect.Bottom);
                    }
                }

                var image = LoadImage(monitor.Path);
                if (image == null) return new WallpaperSlice { Background = _background };

                // Where the full wallpaper image lands on screen, in physical pixels
                double tw = target.Right - target.Left, th = target.Bottom - target.Top;
                double iw = image.PixelWidth, ih = image.PixelHeight;
                Rect placed;
                switch (_position)
                {
                    case DWPOS_STRETCH:
                        placed = new Rect(target.Left, target.Top, tw, th);
                        break;
                    default:
                    {
                        double scale = _position switch
                        {
                            DWPOS_FIT => Math.Min(tw / iw, th / ih),
                            DWPOS_CENTER => 1.0 * (OriginalWidth(monitor.Path, iw) / iw),
                            _ => Math.Max(tw / iw, th / ih) // fill, span, and tile (approximated)
                        };
                        double w = iw * scale, h = ih * scale;
                        placed = new Rect(target.Left + (tw - w) / 2, target.Top + (th - h) / 2, w, h);
                        break;
                    }
                }

                var viewbox = new Rect(
                    (area.Left - placed.X) / placed.Width,
                    (area.Top - placed.Y) / placed.Height,
                    (area.Right - area.Left) / placed.Width,
                    (area.Bottom - area.Top) / placed.Height);

                return new WallpaperSlice { Image = image, Viewbox = viewbox, Background = _background };
            }
            catch (Exception ex)
            {
                Log.Write("Wallpaper lookup failed: " + ex.Message);
                return null;
            }
        }

        private static void Load()
        {
            object? com = null;
            try
            {
                com = new DesktopWallpaperClass();
                var dw = (IDesktopWallpaper)com;
                var list = new List<Monitor>();

                dw.GetMonitorDevicePathCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (dw.GetMonitorDevicePathAt(i, out string id) != 0) continue;
                    if (dw.GetMonitorRECT(id, out RECT rect) != 0) continue; // monitor not attached
                    dw.GetWallpaper(id, out string? path);
                    list.Add(new Monitor { Rect = rect, Path = ResolvePath(path) });
                }

                dw.GetPosition(out _position);
                dw.GetBackgroundColor(out uint colorRef);
                _background = Color.FromRgb((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
                _monitors = list;
            }
            finally
            {
                if (com != null) Marshal.ReleaseComObject(com);
            }
        }

        /// <summary>Falls back to Windows' cached copy (used by slideshows and Spotlight).</summary>
        private static string? ResolvePath(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            return File.Exists(transcoded) ? transcoded : null;
        }

        /// <summary>
        /// Loads the wallpaper at reduced resolution: it's only ever shown blurred, so a quarter of
        /// the detail looks identical and uses a fraction of the memory.
        /// </summary>
        private static BitmapSource? LoadImage(string? path)
        {
            if (path == null) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (Images.TryGetValue(path, out var cached) && cached.stamp == stamp) return cached.image;

            double originalWidth = MeasureWidth(path);
            var bmp = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                if (originalWidth == 0 || originalWidth > 960) bmp.DecodePixelWidth = 960;
                bmp.EndInit();
            }
            bmp.Freeze();

            Images.Clear(); // only keep the current wallpaper(s)
            Images[path] = (stamp, bmp);
            OriginalWidths[path] = originalWidth;
            return bmp;
        }

        private static readonly Dictionary<string, double> OriginalWidths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>For "Center", the image is shown at its real size, so we need its real width.</summary>
        private static double OriginalWidth(string? path, double fallback) =>
            path != null && OriginalWidths.TryGetValue(path, out var w) && w > 0 ? w : fallback;

        private static double MeasureWidth(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return decoder.Frames[0].PixelWidth;
            }
            catch
            {
                return 0;
            }
        }

        // ---------------------------------------------------------------- COM

        private const int DWPOS_CENTER = 0;
        private const int DWPOS_TILE = 1;
        private const int DWPOS_STRETCH = 2;
        private const int DWPOS_FIT = 3;
        private const int DWPOS_FILL = 4;
        private const int DWPOS_SPAN = 5;

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string? wallpaper);
            [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT displayRect);
            [PreserveSig] int SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            [PreserveSig] int SetPosition(int position);
            [PreserveSig] int GetPosition(out int position);
            // Slideshow methods follow; not needed
        }

        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaperClass
        {
        }
    }
}
