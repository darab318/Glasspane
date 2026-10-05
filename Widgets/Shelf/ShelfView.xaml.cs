using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Shelf
{
    /// <summary>
    /// Your newest screenshots and downloads. Click to copy, drag into another app, double-click
    /// to open, right-click for more.
    /// </summary>
    public partial class ShelfView : UserControl
    {
        private readonly ShelfService _service;
        private readonly DispatcherTimer _refreshDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer _clock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        private readonly HashSet<ShelfKind> _dirty = new() { ShelfKind.Screenshots, ShelfKind.Downloads };
        private List<ShelfItem> _items = new();
        private ShelfItem? _pressed;
        private Point _pressedAt;
        private bool _loading;

        public ShelfView(ShelfService service)
        {
            _service = service;
            InitializeComponent();

            // Changes are noted straight away, but the folder is only re-read while the shelf is visible
            _service.Changed += kind => Dispatcher.InvokeAsync(() =>
            {
                _dirty.Add(kind);
                if (kind == CurrentKind && IsVisible)
                {
                    _refreshDebounce.Stop();
                    _refreshDebounce.Start();
                }
            });
            _refreshDebounce.Tick += (_, _) => { _refreshDebounce.Stop(); _ = Reload(); };
            _clock.Tick += (_, _) => { foreach (var i in _items) i.RefreshTime(); };

            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    _clock.Start();
                    if (_dirty.Contains(CurrentKind)) _ = Reload();
                    else foreach (var i in _items) i.RefreshTime();
                }
                else
                {
                    _clock.Stop();
                }
            };

            ApplyLayout();
        }

        private ShelfKind CurrentKind => DownloadsTab.IsChecked == true ? ShelfKind.Downloads : ShelfKind.Screenshots;

        // ---------------------------------------------------------------- loading

        private async Task Reload()
        {
            if (_loading) { _refreshDebounce.Start(); return; }
            _loading = true;
            try
            {
                var kind = CurrentKind;
                _dirty.Remove(kind);
                var items = await Task.Run(() => _service.Read(kind));
                if (kind != CurrentKind) return; // tab changed meanwhile
                _items = items;
                Items.ItemsSource = _items;
                UpdateEmpty();
            }
            finally
            {
                _loading = false;
            }
        }

        private void UpdateEmpty()
        {
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = CurrentKind == ShelfKind.Screenshots
                ? "No screenshots yet.\nPress Win + Print Screen, or save a snip, and it'll appear here."
                : "Your Downloads folder is empty.";
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (Items == null) return; // still loading the layout
            ApplyLayout();
            Scroller.ScrollToTop();
            _items = new List<ShelfItem>();
            Items.ItemsSource = null;
            _ = Reload();
        }

        private void ApplyLayout()
        {
            bool tiles = CurrentKind == ShelfKind.Screenshots;
            Items.ItemsPanel = (ItemsPanelTemplate)FindResource(tiles ? "TilePanel" : "ListPanel");
            Items.ItemTemplate = (DataTemplate)FindResource(tiles ? "TileTemplate" : "RowTemplate");
        }

        // ---------------------------------------------------------------- click, drag, open

        private static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

        private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            if (e.ClickCount == 2)
            {
                _pressed = null;
                Open(item.Path);
                e.Handled = true;
                return;
            }
            _pressed = item;
            _pressedAt = e.GetPosition(this);
        }

        /// <summary>Dragging an item out drops the file into the other app (Discord, a browser, Explorer…).</summary>
        private void Item_MouseMove(object sender, MouseEventArgs e)
        {
            if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - _pressedAt;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var item = _pressed;
            _pressed = null;
            if (!File.Exists(item.Path)) return;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                Log.Write("Drag failed: " + ex.Message);
            }
        }

        private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemOf(sender);
            if (item != null && item == _pressed) Copy(item);
            _pressed = null;
        }

        private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;

            var menu = new ContextMenu();
            AddMenu(menu, "Open", () => Open(item.Path));
            AddMenu(menu, item.IsImage ? "Copy image" : "Copy file", () => Copy(item));
            AddMenu(menu, "Show in folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true }));
            menu.Items.Add(new Separator());
            AddMenu(menu, "Delete (to Recycle Bin)", () => Delete(item));
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static void AddMenu(ContextMenu menu, string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        /// <summary>Puts the file on the clipboard (and the picture itself, for images).</summary>
        private void Copy(ShelfItem item)
        {
            if (!File.Exists(item.Path)) { ShowToast("That file has moved or been deleted"); return; }
            try
            {
                var data = new DataObject();
                data.SetFileDropList(new StringCollection { item.Path });
                if (item.IsImage)
                {
                    var image = new BitmapImage();
                    using (var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        image.BeginInit();
                        image.StreamSource = stream;
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.EndInit();
                    }
                    image.Freeze();
                    data.SetImage(image);
                }
                System.Windows.Clipboard.SetDataObject(data, true);
                ShowToast(item.IsImage ? "Image copied" : "File copied");
            }
            catch (Exception ex)
            {
                Log.Write("Shelf copy failed: " + ex.Message);
                ShowToast("Couldn't copy that");
            }
        }

        private void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("Open failed: " + ex.Message);
                ShowToast("Couldn't open that");
            }
        }

        private void Delete(ShelfItem item)
        {
            if (RecycleBin.Send(item.Path)) ShowToast("Moved to Recycle Bin");
            else ShowToast("Couldn't delete that");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = CurrentKind == ShelfKind.Downloads ? _service.DownloadsFolder : _service.ScreenshotFolders[0].path;
            if (Directory.Exists(folder)) Open(folder);
        }

        private void ShowToast(string message)
        {
            ToastText.Text = message;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
            Toast.BeginAnimation(OpacityProperty, anim);
        }

        /// <summary>On the desktop the tabs only show while you're using the shelf.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            bool show = !onDesktop || active;
            TopRow.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(220)));
            TopRow.IsHitTestVisible = show;
        }

        public void Stop()
        {
            _clock.Stop();
            _refreshDebounce.Stop();
        }
    }

    /// <summary>Deletes a file the way Explorer does: into the Recycle Bin, so it can be restored.</summary>
    internal static class RecycleBin
    {
        public static bool Send(string path)
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = 3,                              // FO_DELETE
                    pFrom = path + "\0",                   // list ends with a double null
                    fFlags = 0x40 | 0x10 | 0x4 | 0x400      // allow undo (Recycle Bin), no confirm, silent, no error UI
                };
                return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
            }
            catch (Exception ex)
            {
                Log.Write("Recycle failed: " + ex.Message);
                return false;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }
}
