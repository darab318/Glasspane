using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Glasspane.Core;
using static Glasspane.Native.NativeMethods;
using WpfClipboard = System.Windows.Clipboard;

namespace Glasspane.Widgets.Clipboard
{
    /// <summary>What was read off the clipboard, before it is saved.</summary>
    public sealed class CapturedItem
    {
        public ClipKind Kind { get; set; }
        public string? Text { get; set; }
        public string? Html { get; set; }
        public string? Rtf { get; set; }
        public string[]? Files { get; set; }
        public byte[]? Png { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? SourceApp { get; set; }
        public string Hash { get; set; } = "";
    }

    /// <summary>Reads from and writes to the Windows clipboard.</summary>
    internal static class ClipboardIO
    {
        public const int MaxTextChars = 10_000_000;     // ~20 MB of text
        public const int MaxFormattedChars = 4_000_000; // HTML / RTF kept alongside text up to this size
        public const long MaxImageBytes = 80_000_000;

        // Formats that apps (password managers, Windows itself) use to say "don't record this"
        private static readonly uint FmtExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        private static readonly uint FmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        private static readonly uint FmtCanInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");

        private static readonly ConcurrentDictionary<uint, string> AppNames = new();

        // ------------------------------------------------------------------ reading

        public static async Task<CapturedItem?> CaptureAsync()
        {
            if (IsClipboardFormatAvailable(FmtExclude) || IsClipboardFormatAvailable(FmtViewerIgnore))
                return null;

            string? source = GetOwnerAppName();

            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    var (item, pendingImage) = Read();
                    if (item == null) return null;
                    item.SourceApp = source;

                    // Heavy work (PNG encoding, hashing) happens off the UI thread
                    await Task.Run(() =>
                    {
                        if (pendingImage != null) item.Png = EncodePng(pendingImage);
                        item.Hash = ComputeHash(item);
                    });

                    if (item.Kind == ClipKind.Image && (item.Png == null || item.Png.LongLength > MaxImageBytes))
                        return null;
                    return item;
                }
                catch (ExternalException)
                {
                    // Another app has the clipboard open; try again shortly
                    await Task.Delay(40 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Log.Write("Clipboard read failed: " + ex.Message);
                    return null;
                }
            }
            return null;
        }

        private static (CapturedItem? item, BitmapSource? pendingImage) Read()
        {
            IDataObject? data = WpfClipboard.GetDataObject();
            if (data == null) return (null, null);

            // Windows' own "don't put this in clipboard history" flag
            if (IsClipboardFormatAvailable(FmtCanInclude) &&
                data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag)
            {
                var bytes = flag.ToArray();
                if (bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == 0) return (null, null);
            }

            // Files copied in Explorer
            if (data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                return (new CapturedItem { Kind = ClipKind.Files, Files = files }, null);
            }

            // Text (keeping HTML / RTF so formatting survives when pasted back)
            if (data.GetDataPresent(DataFormats.UnicodeText, true) &&
                data.GetData(DataFormats.UnicodeText, true) is string text &&
                !string.IsNullOrWhiteSpace(text))
            {
                if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);
                return (new CapturedItem
                {
                    Kind = ClipKind.Text,
                    Text = text,
                    Html = ReadFormatted(data, DataFormats.Html),
                    Rtf = ReadFormatted(data, DataFormats.Rtf)
                }, null);
            }

            // Images: prefer the PNG format (keeps transparency) when the source app offers it
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream && pngStream.Length > 0)
            {
                var png = pngStream.ToArray();
                var (w, h) = MeasurePng(png);
                if (w > 0) return (new CapturedItem { Kind = ClipKind.Image, Png = png, Width = w, Height = h }, null);
            }

            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                BitmapSource? bmp = WpfClipboard.GetImage();
                if (bmp != null)
                {
                    bmp.Freeze();
                    return (new CapturedItem { Kind = ClipKind.Image, Width = bmp.PixelWidth, Height = bmp.PixelHeight }, bmp);
                }
            }

            return (null, null);
        }

        private static string? ReadFormatted(IDataObject data, string format)
        {
            try
            {
                if (data.GetDataPresent(format) && data.GetData(format) is string s && s.Length <= MaxFormattedChars)
                    return s;
            }
            catch
            {
                // Some apps advertise formats they can't actually render
            }
            return null;
        }

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static (int w, int h) MeasurePng(byte[] png)
        {
            try
            {
                using var ms = new MemoryStream(png);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                return (frame.PixelWidth, frame.PixelHeight);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static string ComputeHash(CapturedItem item)
        {
            using var sha = SHA256.Create();
            byte[] content = item.Kind switch
            {
                ClipKind.Text => Encoding.UTF8.GetBytes(item.Text ?? ""),
                ClipKind.Files => Encoding.UTF8.GetBytes(string.Join("\n", item.Files ?? Array.Empty<string>())),
                _ => item.Png ?? Array.Empty<byte>()
            };
            var prefix = new[] { (byte)item.Kind };
            sha.TransformBlock(prefix, 0, 1, null, 0);
            sha.TransformFinalBlock(content, 0, content.Length);
            return Convert.ToHexString(sha.Hash!);
        }

        /// <summary>Name of the app that put the data on the clipboard, e.g. "Google Chrome".</summary>
        private static string? GetOwnerAppName()
        {
            try
            {
                IntPtr owner = GetClipboardOwner();
                if (owner == IntPtr.Zero) return null;
                GetWindowThreadProcessId(owner, out uint pid);
                if (pid == 0) return null;
                if (AppNames.TryGetValue(pid, out var cached)) return cached;

                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;
                try
                {
                    string? desc = proc.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) name = desc.Trim();
                }
                catch
                {
                    // Elevated / protected processes don't let us read their details
                }
                AppNames[pid] = name;
                return name;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>Puts a history item back on the clipboard.</summary>
        public static bool Write(ClipboardStore store, ClipEntry entry, bool plainText = false)
        {
            var data = new DataObject();

            switch (entry.Kind)
            {
                case ClipKind.Text:
                {
                    var (text, html, rtf) = store.GetFullText(entry.Id);
                    if (string.IsNullOrEmpty(text)) return false;
                    data.SetData(DataFormats.UnicodeText, text);
                    if (!plainText)
                    {
                        if (html != null) data.SetData(DataFormats.Html, html);
                        if (rtf != null) data.SetData(DataFormats.Rtf, rtf);
                    }
                    break;
                }
                case ClipKind.Image:
                {
                    if (entry.ImagePath == null || !File.Exists(entry.ImagePath)) return false;
                    byte[] bytes = File.ReadAllBytes(entry.ImagePath);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    data.SetImage(bmp);
                    data.SetData("PNG", new MemoryStream(bytes)); // for apps that keep transparency
                    break;
                }
                case ClipKind.Files:
                {
                    if (plainText)
                    {
                        data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, entry.Files));
                    }
                    else
                    {
                        var list = new StringCollection();
                        list.AddRange(entry.Files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray());
                        if (list.Count == 0) return false;
                        data.SetFileDropList(list);
                    }
                    break;
                }
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    WpfClipboard.SetDataObject(data, true);
                    return true;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }
    }
}
