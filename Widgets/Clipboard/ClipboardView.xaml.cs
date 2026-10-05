using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glasspane.Core;
using Glasspane.Native;

namespace Glasspane.Widgets.Clipboard
{
    public partial class ClipboardView : UserControl
    {
        private const int PageSize = 200;

        private readonly ClipboardStore _store;
        private readonly ForegroundTracker _foreground;
        private readonly int _historyLimit;
        private readonly ObservableCollection<ClipEntry> _items = new();
        private readonly DispatcherTimer _captureDebounce;
        private readonly DispatcherTimer _searchDebounce;
        private readonly DispatcherTimer _clock;

        private ClipFilter _filter = ClipFilter.All;
        private string _search = "";
        private bool _hasMore;
        private bool _ready;
        private bool _capturing;
        private bool _captureAgain;
        private int _addedSinceTrim;

        // When we put something on the clipboard ourselves, the resulting change is ours, not new
        private DateTime _ignoreUntilUtc = DateTime.MinValue;
        private long _ignoreEntryId;

        public ClipboardView(ClipboardStore store, ClipboardMonitor monitor, ForegroundTracker foreground, int historyLimit)
        {
            _store = store;
            _foreground = foreground;
            _historyLimit = historyLimit;

            // Apps often update the clipboard several times in a row; wait for it to settle
            _captureDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _captureDebounce.Tick += (_, _) => { _captureDebounce.Stop(); OnClipboardChanged(); };

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); Reload(); };

            _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _clock.Tick += (_, _) => { foreach (var e in _items) e.RefreshTime(); };

            // "5 min ago" labels only need updating while you can see them
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    foreach (var e in _items) e.RefreshTime();
                    _clock.Start();
                }
                else
                {
                    _clock.Stop();
                }
            };

            InitializeComponent();
            List.ItemsSource = _items;

            monitor.Changed += (_, _) => { _captureDebounce.Stop(); _captureDebounce.Start(); };

            _store.Trim(_historyLimit);
            _ready = true;
            Reload();
        }

        // ---------------------------------------------------------------- loading

        private void Reload()
        {
            if (!_ready) return;
            _items.Clear();
            LoadMore();
            if (_items.Count > 0)
            {
                List.SelectedIndex = 0;
                List.ScrollIntoView(_items[0]);
            }
        }

        private void LoadMore()
        {
            var page = _store.Query(_search, _filter, PageSize, _items.Count);
            foreach (var e in page) _items.Add(e);
            _hasMore = page.Count == PageSize;
            UpdateStatus();
        }

        private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_hasMore && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 400)
                LoadMore();
        }

        private void UpdateStatus()
        {
            int total = _store.Count();
            StatusText.Text = total == 1 ? "1 item" : $"{total:N0} items";

            bool empty = _items.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = total == 0
                ? "Copy something and it will appear here"
                : "Nothing matches";
        }

        private bool Matches(ClipEntry e)
        {
            switch (_filter)
            {
                case ClipFilter.Text when !e.IsText:
                case ClipFilter.Images when !e.IsImage:
                case ClipFilter.Files when !e.IsFiles:
                case ClipFilter.Pinned when !e.Pinned:
                    return false;
            }
            if (string.IsNullOrWhiteSpace(_search)) return true;
            string q = _search.Trim();
            return e.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.Files.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase))
                   || (e.SourceApp?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        // ---------------------------------------------------------------- capturing

        private async void OnClipboardChanged()
        {
            if (_capturing)
            {
                _captureAgain = true;
                return;
            }

            _capturing = true;
            try
            {
                do
                {
                    _captureAgain = false;

                    if (DateTime.UtcNow < _ignoreUntilUtc)
                    {
                        MarkUsed(_ignoreEntryId);
                        continue;
                    }

                    var item = await ClipboardIO.CaptureAsync();
                    if (item == null) continue;

                    long id = _store.Upsert(item, out bool isNew);
                    if (isNew && ++_addedSinceTrim >= 50)
                    {
                        _addedSinceTrim = 0;
                        _store.Trim(_historyLimit);
                    }

                    var entry = _store.Get(id);
                    if (entry != null) InsertAtTop(entry);
                    UpdateStatus();
                }
                while (_captureAgain);
            }
            catch (Exception ex)
            {
                Log.Write("Capture failed: " + ex);
            }
            finally
            {
                _capturing = false;
            }
        }

        private void InsertAtTop(ClipEntry entry)
        {
            var existing = _items.FirstOrDefault(x => x.Id == entry.Id);
            if (existing != null) _items.Remove(existing);
            if (!Matches(entry)) return;

            _items.Insert(0, entry);
            List.SelectedIndex = 0;
            List.ScrollIntoView(entry);
        }

        /// <summary>
        /// Marks an item as just used. It moves to the top next time the list refreshes,
        /// not immediately, so the list doesn't shift under the mouse mid double-click.
        /// </summary>
        private void MarkUsed(long id)
        {
            DateTime when = _store.Touch(id);
            var entry = _items.FirstOrDefault(x => x.Id == id);
            if (entry != null) entry.LastUsedUtc = when;
        }

        // ---------------------------------------------------------------- actions

        private bool Copy(ClipEntry entry, bool plainText = false)
        {
            _ignoreEntryId = entry.Id;
            _ignoreUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

            bool ok;
            try
            {
                ok = ClipboardIO.Write(_store, entry, plainText);
            }
            catch (Exception ex)
            {
                Log.Write("Copy failed: " + ex.Message);
                ok = false;
            }

            if (!ok)
            {
                _ignoreUntilUtc = DateTime.MinValue;
                ShowToast(entry.IsFiles ? "Those files no longer exist" : "Couldn't copy that");
            }
            return ok;
        }

        private async void Paste(ClipEntry entry, bool plainText = false)
        {
            if (!Copy(entry, plainText)) return;
            bool pasted = await KeyboardSender.PasteIntoAsync(_foreground.LastExternalWindow);
            if (!pasted) ShowToast("Copied");
        }

        private void TogglePin(ClipEntry entry)
        {
            bool pinned = !entry.Pinned;
            _store.SetPinned(entry.Id, pinned);
            entry.Pinned = pinned;
            if (_filter == ClipFilter.Pinned && !pinned) _items.Remove(entry);
            ShowToast(pinned ? "Pinned" : "Unpinned");
        }

        private void Delete(ClipEntry entry)
        {
            int index = _items.IndexOf(entry);
            _store.Delete(entry.Id);
            _items.Remove(entry);
            if (_items.Count > 0) List.SelectedIndex = Math.Min(index, _items.Count - 1);
            UpdateStatus();
        }

        /// <summary>
        /// On the desktop, search, filters and the status line fade out when you're not using
        /// the widget, leaving just your clipboard items on the wallpaper. They keep their
        /// space so items never jump under the mouse.
        /// </summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            bool show = !onDesktop || active || !string.IsNullOrEmpty(_search);
            var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(220));
            foreach (UIElement el in new UIElement[] { SearchRow, FilterRow, StatusRow })
            {
                el.BeginAnimation(OpacityProperty, anim);
                el.IsHitTestVisible = show;
            }
        }

        public void FocusSearch()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrEmpty(_search)) Reload(); // brings recently used items to the top
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
                if (_items.Count > 0)
                {
                    List.SelectedIndex = 0;
                    List.ScrollIntoView(_items[0]);
                }
            }, DispatcherPriority.Input);
        }

        private void ShowToast(string message)
        {
            ToastText.Text = message;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
            Toast.BeginAnimation(OpacityProperty, anim);
        }

        private static ClipEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipEntry;

        private ClipEntry? Selected => List.SelectedItem as ClipEntry;

        // ---------------------------------------------------------------- mouse

        private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (EntryOf(sender) is { } entry && Copy(entry)) ShowToast("Copied");
        }

        private void Item_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (EntryOf(sender) is { } entry) Paste(entry);
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) Paste(entry);
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) TogglePin(entry);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) Delete(entry);
        }

        // ---------------------------------------------------------------- context menu

        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry && Copy(entry)) ShowToast("Copied");
        }

        private void MenuCopyPlain_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry && Copy(entry, plainText: true)) ShowToast("Copied as plain text");
        }

        private void MenuPaste_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) Paste(entry);
        }

        private void MenuPin_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) TogglePin(entry);
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { } entry) Delete(entry);
        }

        private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { Files.Length: > 0 } entry)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Files[0]}\"") { UseShellExecute = true });
        }

        private void MenuOpenImage_Click(object sender, RoutedEventArgs e)
        {
            if (EntryOf(sender) is { ImagePath: { } path } && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = MoreButton.ContextMenu;
            menu.PlacementTarget = MoreButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "Delete all clipboard history? Pinned items will be kept.",
                "Glasspane", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            int removed = _store.ClearUnpinned();
            Reload();
            ShowToast($"Cleared {removed:N0} items");
        }

        private void OpenImagesFolder_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.ImageFolder}\"") { UseShellExecute = true });

        // ---------------------------------------------------------------- search, filters, keyboard

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = SearchBox.Text;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out ClipFilter filter))
            {
                _filter = filter;
                Reload();
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down when _items.Count > 0:
                    FocusItem(Math.Max(0, List.SelectedIndex));
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if ((Selected ?? _items.FirstOrDefault()) is { } entry) Paste(entry);
                    e.Handled = true;
                    break;
                case Key.Escape when SearchBox.Text.Length > 0:
                    SearchBox.Clear();
                    e.Handled = true;
                    break;
            }
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            var entry = Selected;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.Up && List.SelectedIndex <= 0)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (entry == null)
            {
                return;
            }
            else if (e.Key == Key.Enter)
            {
                Paste(entry, plainText: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                Delete(entry);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.C)
            {
                if (Copy(entry)) ShowToast("Copied");
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.P)
            {
                TogglePin(entry);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.F)
            {
                FocusSearch();
                e.Handled = true;
            }
        }

        private void FocusItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            List.SelectedIndex = index;
            List.ScrollIntoView(_items[index]);
            List.UpdateLayout();
            if (List.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        }
    }
}
