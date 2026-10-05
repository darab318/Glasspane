using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Shelf
{
    public enum ShelfKind
    {
        Screenshots,
        Downloads
    }

    /// <summary>A file on the shelf.</summary>
    public sealed class ShelfItem : INotifyPropertyChanged
    {
        private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".jfif" };

        private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

        private WeakReference<ImageSource>? _thumbnail;
        private int _attempts;

        public ShelfItem(FileInfo file)
        {
            Path = file.FullName;
            Name = file.Name;
            Size = file.Length;
            Modified = file.LastWriteTime;
            IsImage = ImageTypes.Contains(file.Extension);
        }

        public string Path { get; }
        public string Name { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public bool IsImage { get; }

        public string TimeText => Relative(Modified);
        public string MetaText => $"{SizeText(Size)}  ·  {Relative(Modified)}";

        /// <summary>
        /// A small preview for images (decoded at tile size, freed when scrolled away), or the file
        /// type's icon for everything else.
        /// </summary>
        public ImageSource? Thumbnail
        {
            get
            {
                if (!IsImage) return IconFor(Path);
                if (_thumbnail != null && _thumbnail.TryGetTarget(out var cached)) return cached;
                try
                {
                    var image = new BitmapImage();
                    using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        image.BeginInit();
                        image.StreamSource = stream;
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 320;
                        image.EndInit();
                    }
                    image.Freeze();
                    _thumbnail = new WeakReference<ImageSource>(image);
                    return image;
                }
                catch
                {
                    // Probably still being saved: try again shortly (a few times)
                    if (++_attempts <= 3)
                    {
                        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                        retry.Tick += (_, _) => { retry.Stop(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); };
                        retry.Start();
                    }
                    return IconFor(Path);
                }
            }
        }

        private static ImageSource? IconFor(string path)
        {
            string ext = System.IO.Path.GetExtension(path);
            // .exe and .lnk files have their own icons; everything else shares one per type
            string key = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? path : ext;
            return IconCache.GetOrAdd(key, _ =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon == null) return null;
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                catch
                {
                    return null;
                }
            });
        }

        public void RefreshTime()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetaText)));
        }

        private static string Relative(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalSeconds < 45) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (time.Date == DateTime.Today) return time.ToString("HH:mm");
            if (time.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + time.ToString("HH:mm");
            if (span.TotalDays < 7) return time.ToString("ddd HH:mm");
            return time.ToString("d MMM");
        }

        private static string SizeText(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
            if (bytes >= 1L << 10) return $"{bytes / 1024.0:0} KB";
            return $"{bytes} B";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Watches the screenshot and download folders. Windows tells us when files are added or
    /// removed, so nothing is scanned on a timer; the folder is only re-read after a change.
    /// </summary>
    public sealed class ShelfService : IDisposable
    {
        private const int MaxItems = 40;

        private static readonly HashSet<string> PartialDownloads = new(StringComparer.OrdinalIgnoreCase)
            { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

        private readonly List<FileSystemWatcher> _watchers = new();

        public ShelfService()
        {
            ScreenshotFolders = new List<(string path, bool recursive)>();
            string? screenshots = KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"));
            screenshots ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            ScreenshotFolders.Add((screenshots, false));

            // ShareX saves into month folders under Documents\ShareX\Screenshots
            string sharex = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Screenshots");
            if (Directory.Exists(sharex)) ScreenshotFolders.Add((sharex, true));

            DownloadsFolder = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))
                              ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            foreach (var (path, recursive) in ScreenshotFolders) Watch(path, recursive, ShelfKind.Screenshots);
            Watch(DownloadsFolder, false, ShelfKind.Downloads);
        }

        public List<(string path, bool recursive)> ScreenshotFolders { get; }
        public string DownloadsFolder { get; }

        /// <summary>Files were added, removed or renamed in a folder (raised on a background thread).</summary>
        public event Action<ShelfKind>? Changed;

        private void Watch(string folder, bool recursive, ShelfKind kind)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = recursive,
                    // Only names: files appearing, disappearing or being renamed (a finished download is
                    // renamed from .crdownload/.part). Not size changes, which fire constantly mid-download.
                    NotifyFilter = NotifyFilters.FileName
                };
                watcher.Created += (_, _) => Changed?.Invoke(kind);
                watcher.Deleted += (_, _) => Changed?.Invoke(kind);
                watcher.Renamed += (_, _) => Changed?.Invoke(kind);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Write($"Can't watch {folder}: {ex.Message}");
            }
        }

        /// <summary>The newest files of a kind. Safe to call on a background thread.</summary>
        public List<ShelfItem> Read(ShelfKind kind)
        {
            var files = new List<FileInfo>();
            try
            {
                if (kind == ShelfKind.Screenshots)
                {
                    foreach (var (path, recursive) in ScreenshotFolders)
                        files.AddRange(Enumerate(path, recursive));
                    files = files.Where(f => IsImageFile(f)).ToList();
                }
                else
                {
                    files.AddRange(Enumerate(DownloadsFolder, false)
                        .Where(f => !PartialDownloads.Contains(f.Extension) && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Shelf read failed: " + ex.Message);
            }

            return files.OrderByDescending(f => f.LastWriteTime).Take(MaxItems).Select(f => new ShelfItem(f)).ToList();
        }

        private static IEnumerable<FileInfo> Enumerate(string folder, bool recursive)
        {
            if (!Directory.Exists(folder)) return Enumerable.Empty<FileInfo>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            return new DirectoryInfo(folder).EnumerateFiles("*", options);
        }

        private static bool IsImageFile(FileInfo f) =>
            f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".jfif";

        private static string? KnownFolder(Guid id)
        {
            try
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr ptr) != 0) return null;
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
