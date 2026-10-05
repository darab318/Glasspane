using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glasspane.Widgets.Clipboard
{
    public enum ClipKind
    {
        Text = 0,
        Image = 1,
        Files = 2
    }

    public enum ClipFilter
    {
        All,
        Text,
        Images,
        Files,
        Pinned
    }

    /// <summary>One item in the clipboard history, as shown in the list.</summary>
    public sealed class ClipEntry : INotifyPropertyChanged
    {
        private DateTime _lastUsedUtc;
        private bool _pinned;
        // Weak, so thumbnails of cards scrolled out of view can be freed and re-made if needed
        private WeakReference<ImageSource>? _thumbnail;
        private bool _thumbnailFailed;

        public long Id { get; init; }
        public ClipKind Kind { get; init; }

        /// <summary>The first few hundred characters; the full text is loaded only when needed.</summary>
        public string Preview { get; init; } = "";
        public int CharCount { get; init; }
        public string[] Files { get; init; } = Array.Empty<string>();
        public string? ImagePath { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string? SourceApp { get; init; }
        public DateTime CreatedUtc { get; init; }
        public string Hash { get; init; } = "";

        public DateTime LastUsedUtc
        {
            get => _lastUsedUtc;
            set { _lastUsedUtc = value; Changed(nameof(LastUsedUtc)); Changed(nameof(MetaText)); }
        }

        public bool Pinned
        {
            get => _pinned;
            set { _pinned = value; Changed(nameof(Pinned)); Changed(nameof(PinMenuText)); }
        }

        public bool IsText => Kind == ClipKind.Text;
        public bool IsImage => Kind == ClipKind.Image;
        public bool IsFiles => Kind == ClipKind.Files;

        public string PinMenuText => Pinned ? "Unpin" : "Pin";

        public string FilesTitle => Files.Length == 1 ? Path.GetFileName(Files[0].TrimEnd('\\')) : $"{Files.Length} items";

        public string FilesDetail
        {
            get
            {
                if (Files.Length == 1) return Path.GetDirectoryName(Files[0]) ?? Files[0];
                var names = Files.Take(4).Select(f => Path.GetFileName(f.TrimEnd('\\')));
                string list = string.Join(", ", names);
                return Files.Length > 4 ? $"{list} +{Files.Length - 4} more" : list;
            }
        }

        /// <summary>
        /// Small preview image, decoded at roughly the size it's shown (not the full screenshot),
        /// only when the card scrolls into view.
        /// </summary>
        public ImageSource? Thumbnail
        {
            get
            {
                if (ImagePath == null || _thumbnailFailed) return null;
                if (_thumbnail != null && _thumbnail.TryGetTarget(out var cached)) return cached;
                try
                {
                    if (!File.Exists(ImagePath)) { _thumbnailFailed = true; return null; }
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(ImagePath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    // Cards show images up to 150 px tall and ~340 px wide; decode at about 2x for sharp
                    // high-DPI screens. A 4K screenshot then takes ~0.4 MB instead of ~33 MB.
                    if (Width > 0 && Height > 0)
                    {
                        if ((double)Width / Height > 680.0 / 300.0)
                            bmp.DecodePixelWidth = Math.Min(Width, 680);
                        else
                            bmp.DecodePixelHeight = Math.Min(Height, 300);
                    }
                    bmp.EndInit();
                    bmp.Freeze();
                    _thumbnail = new WeakReference<ImageSource>(bmp);
                    return bmp;
                }
                catch
                {
                    _thumbnailFailed = true;
                    return null;
                }
            }
        }

        public string MetaText
        {
            get
            {
                var parts = new List<string>(3);
                if (!string.IsNullOrEmpty(SourceApp)) parts.Add(SourceApp!);
                parts.Add(Relative(LastUsedUtc));
                switch (Kind)
                {
                    case ClipKind.Text:
                        parts.Add(CharCount == 1 ? "1 char" : $"{CharCount:N0} chars");
                        break;
                    case ClipKind.Image:
                        if (Width > 0) parts.Add($"{Width}×{Height}");
                        break;
                }
                return string.Join("  ·  ", parts);
            }
        }

        public void RefreshTime() => Changed(nameof(MetaText));

        private static string Relative(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 45) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
            var local = utc.ToLocalTime();
            if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
            if (span.TotalDays < 7) return local.ToString("ddd HH:mm");
            return local.Year == DateTime.Today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
