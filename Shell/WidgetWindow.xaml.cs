using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Glasspane.Core;
using Glasspane.Native;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Shell
{
    /// <summary>
    /// The container widgets live in. One window can hold several widgets stacked together;
    /// any of them can be split off into its own window, and windows join by dragging one onto
    /// another. In window mode it's a tinted glass panel with a
    /// taskbar button. On the desktop the panel, border and controls dissolve away so only
    /// the content is left sitting on the wallpaper: no taskbar button, not in Alt+Tab,
    /// always underneath other windows, and it survives Win+D.
    /// </summary>
    public partial class WidgetWindow : Window
    {
        private static readonly Color Tint = Color.FromRgb(0x16, 0x1A, 0x22);
        private static readonly Duration Smooth = TimeSpan.FromMilliseconds(380);

        // Soft shadow under text and icons so they stay readable on any wallpaper,
        // the same trick Windows uses for desktop icon labels.
        private static readonly DropShadowEffect TextShadow = CreateShadow();

        private readonly List<IWidget> _widgets = new();
        private readonly List<UIElement> _sectionButtons = new();
        private readonly WidgetManager _manager;
        private readonly WidgetSettings _settings;
        private readonly Action _save;
        private readonly DispatcherTimer _saveDebounce;
        private IntPtr _hwnd;
        private bool _pinned;
        private bool _peeking;
        private bool _allowClose;
        private bool _loadingSlider;
        private DateTime _popupClosedAt;
        private bool _dragging;
        private (bool pinned, bool active)? _revealState;
        private readonly DispatcherTimer _blurRefresh;
        private bool _wallpaperBlurOn;

        public WidgetWindow(WidgetSettings settings, IEnumerable<IWidget> widgets, WidgetManager manager)
        {
            InitializeComponent();
            _settings = settings;
            _manager = manager;
            _save = manager.Save;
            _pinned = settings.Mode == WidgetMode.Pinned;

            _widgets.AddRange(widgets);
            BuildSections();

            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); CapturePlacement(); _save(); };

            // While moving or resizing, the wallpaper blur follows at up to ~20 updates a second
            _blurRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _blurRefresh.Tick += (_, _) => { _blurRefresh.Stop(); UpdateBlur(); };

            RestorePlacement();
            LockToggle.IsChecked = settings.Locked;
            SyncAppearanceControls();
            ApplyLook(animate: false);
            UpdateChrome();

            SourceInitialized += OnSourceInitialized;
            LocationChanged += (_, _) =>
            {
                QueueSave();
                if (_dragging) _manager.UpdateDrag(this);
                QueueBlurRefresh();
            };
            SizeChanged += (_, _) =>
            {
                QueueSave();
                BlurHost.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
                QueueBlurRefresh();
            };
            Loaded += (_, _) => UpdateBlur();
            StateChanged += OnStateChanged;
            Deactivated += (_, _) => EndPeek();
            MouseEnter += (_, _) => UpdateReveal();
            MouseLeave += (_, _) => UpdateReveal();
            IsKeyboardFocusWithinChanged += (_, _) => UpdateReveal();
            SettingsPopup.Closed += (_, _) => { _popupClosedAt = DateTime.UtcNow; UpdateReveal(); };
        }

        public IReadOnlyList<IWidget> Widgets => _widgets;
        public WidgetSettings Settings => _settings;
        public IntPtr Handle => _hwnd;
        public bool IsPinned => _pinned;
        public string DisplayTitle => string.Join(" + ", _widgets.Select(w => w.Title));
        public event EventHandler? ModeChanged;

        private static DropShadowEffect CreateShadow()
        {
            var fx = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 9,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.7,
                RenderingBias = RenderingBias.Performance
            };
            fx.Freeze();
            return fx;
        }

        // ---------------------------------------------------------------- setup

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

            if (_pinned) ApplyDesktopStyles(true);
            UpdateBlur();
        }

        private void RestorePlacement()
        {
            var defaults = (_widgets.FirstOrDefault(w => w.FillsHeight) ?? _widgets.FirstOrDefault())?.DefaultSize
                           ?? new Size(380, 560);
            Width = _settings.Width ?? defaults.Width;
            if (_widgets.Any(w => w.FillsHeight))
                Height = _settings.Height ?? defaults.Height;

            if (_settings.Left is double left && _settings.Top is double top && IsOnScreen(left, top, Width, double.IsNaN(Height) ? 200 : Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>Guards against a monitor having been unplugged since last time.</summary>
        private static bool IsOnScreen(double left, double top, double width, double height)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(screen, new Rect(left, top, width, height));
            return !visible.IsEmpty && visible.Width > 80 && visible.Height > 40;
        }

        private void CapturePlacement()
        {
            // Skip if the window hasn't been laid out yet (e.g. it started hidden)
            if (WindowState != WindowState.Normal || double.IsNaN(Left) || double.IsNaN(Top) || ActualWidth <= 0) return;
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = ActualWidth;
            _settings.Height = ActualHeight;
        }

        private void QueueSave()
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        // ---------------------------------------------------------------- window procedure

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // On the desktop, any attempt to bring the window forward is redirected to the bottom,
            // so you can click and use it without it covering your other windows.
            // Compact widgets (no list to fill space) size to their content after a manual resize
            if (msg == WM_EXITSIZEMOVE && !_widgets.Any(w => w.FillsHeight))
                SizeToContent = SizeToContent.Height;

            // Wallpaper, screen layout or scaling changed: the blurred wallpaper copy needs redoing
            if (msg == 0x001A /* WM_SETTINGCHANGE */ || msg == 0x007E /* WM_DISPLAYCHANGE */ || msg == 0x02E0 /* WM_DPICHANGED */)
            {
                Wallpaper.Invalidate();
                QueueBlurRefresh();
            }

            if (msg == WM_WINDOWPOSCHANGING && _pinned && !_peeking)
            {
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOZORDER) == 0)
                {
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            // "Show desktop" and Win+M try to minimise everything; desktop widgets stay put.
            if (_pinned && WindowState == WindowState.Minimized)
                Dispatcher.InvokeAsync(() => { WindowState = WindowState.Normal; }, DispatcherPriority.Background);
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }

        // ---------------------------------------------------------------- modes

        /// <summary>
        /// Switches between window and desktop. The panel, border and controls animate in or
        /// out, so the widget visibly melts into the wallpaper (or lifts out of it).
        /// </summary>
        public void SetPinned(bool pinned)
        {
            if (pinned == _pinned) return;
            SettingsPopup.IsOpen = false;

            _pinned = pinned;
            _peeking = false;
            _settings.Mode = pinned ? WidgetMode.Pinned : WidgetMode.Window;

            if (_hwnd != IntPtr.Zero) ApplyDesktopStyles(pinned);

            SyncAppearanceControls();
            ApplyLook(animate: true);
            UpdateChrome();
            _save();
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyDesktopStyles(bool pinned)
        {
            if (pinned)
            {
                Topmost = false;
                ShowInTaskbar = false;

                // Owning the window by the desktop (Progman) is what keeps it visible on Win+D
                IntPtr desktop = FindWindow("Progman", null);
                SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, desktop);

                long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
                ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW; // hides it from Alt+Tab
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            else
            {
                SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, IntPtr.Zero);

                long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
                ex &= ~WS_EX_TOOLWINDOW;
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

                ShowInTaskbar = true;
                SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
                Activate();
            }
        }

        /// <summary>
        /// Shows the widget. On the desktop (possibly covered by other windows) it is lifted
        /// above everything until you click away, then sinks back into the desktop.
        /// </summary>
        public void Summon(IWidget? focus = null)
        {
            PowerSaver.Wake();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            _settings.Visible = true;

            if (_pinned)
            {
                _peeking = true;
                SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            }
            Activate();
            SetForegroundWindow(_hwnd);
            (focus ?? _widgets.FirstOrDefault())?.OnSummoned();
        }

        private void EndPeek()
        {
            if (!_peeking) return;
            _peeking = false;
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ---------------------------------------------------------------- look

        private double CurrentOpacity
        {
            get => _pinned ? _settings.DesktopOpacity : _settings.BackgroundOpacity;
            set { if (_pinned) _settings.DesktopOpacity = value; else _settings.BackgroundOpacity = value; }
        }

        /// <summary>Blur strength for the current mode, 0–1.</summary>
        private double CurrentBlur
        {
            get => _pinned ? _settings.DesktopBlurStrength : _settings.BlurStrength;
            set { if (_pinned) _settings.DesktopBlurStrength = value; else _settings.BlurStrength = value; }
        }

        private void QueueBlurRefresh()
        {
            if (_wallpaperBlurOn && !_blurRefresh.IsEnabled) _blurRefresh.Start();
        }

        /// <summary>
        /// On the desktop, blur is drawn from a copy of the wallpaper behind the widget, so its
        /// strength can be anything. As a window there may be other windows behind it, so it uses
        /// Windows' own blur, which has one fixed strength.
        /// </summary>
        private void UpdateBlur()
        {
            if (_hwnd == IntPtr.Zero) return;
            double strength = Math.Clamp(CurrentBlur, 0, 1);
            _wallpaperBlurOn = strength > 0 && _pinned && ShowWallpaperBlur(strength);
            if (!_wallpaperBlurOn) BlurHost.Visibility = Visibility.Collapsed;
            Backdrop.SetBlur(_hwnd, strength > 0 && !_wallpaperBlurOn);
        }

        private bool ShowWallpaperBlur(double strength)
        {
            double radius = 4 + strength * 56; // in DIPs
            var dpi = VisualTreeHelper.GetDpi(this);
            int pad = (int)Math.Ceiling(radius * dpi.DpiScaleX); // extra wallpaper around the edges so they blur cleanly
            if (!GetWindowRect(_hwnd, out RECT r)) return false;
            var area = new RECT { Left = r.Left - pad, Top = r.Top - pad, Right = r.Right + pad, Bottom = r.Bottom + pad };

            var slice = Wallpaper.GetSlice(area);
            if (slice == null) return false;

            var fill = new SolidColorBrush(slice.Background);
            fill.Freeze();
            WallpaperFill.Fill = fill;

            if (slice.Image != null)
            {
                var brush = new ImageBrush(slice.Image)
                {
                    Viewbox = slice.Viewbox,
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.None
                };
                brush.Freeze();
                WallpaperImage.Fill = brush;
            }
            else
            {
                WallpaperImage.Fill = null;
            }

            BlurLayer.Margin = new Thickness(-pad / dpi.DpiScaleX);
            if (BlurLayer.Effect is BlurEffect fx)
                fx.Radius = radius;
            else
                BlurLayer.Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };

            // Blur once, then reuse the result until something changes
            BlurLayer.CacheMode ??= new BitmapCache();
            BlurHost.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
            BlurHost.Visibility = Visibility.Visible;
            return true;
        }

        private void ApplyLook(bool animate)
        {
            double o = Math.Clamp(CurrentOpacity, 0, 1);
            Duration d = animate ? Smooth : new Duration(TimeSpan.Zero);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            // Panel tint
            var brush = (SolidColorBrush)Panel.Background;
            var target = Color.FromArgb((byte)Math.Round(o * 255), Tint.R, Tint.G, Tint.B);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, d) { EasingFunction = ease });

            // Border: always there for a window; on the desktop it only appears with a visible panel
            double edge = _pinned ? Math.Min(1, o * 2.5) : 1;
            Edge.BeginAnimation(OpacityProperty, new DoubleAnimation(edge, d) { EasingFunction = ease });

            // Text shadows when the wallpaper shows through
            var fx = o < 0.6 ? TextShadow : null;
            Sections.Effect = fx;
            TitleLabel.Effect = fx;

            // Item cards: faint frosted plates on the wallpaper, subtle on a solid panel
            byte cardAlpha = (byte)Math.Round(0x1C + (0x10 - 0x1C) * o);
            var card = new SolidColorBrush(Color.FromArgb(cardAlpha, 0xFF, 0xFF, 0xFF));
            card.Freeze();
            Resources["CardBackground"] = card;

            UpdateBlur();
            UpdateReveal();
        }

        /// <summary>
        /// On the desktop, buttons and the widget's own controls (search, filters) only show
        /// while you're using it. The rest of the time it's just content on the wallpaper.
        /// </summary>
        private void UpdateReveal()
        {
            bool active = IsMouseOver || IsKeyboardFocusWithin || SettingsPopup.IsOpen;
            PowerSaver.Report(this, active && IsVisible);

            // Only animate when something actually changed: every animation frame of a
            // transparent window costs a full redraw
            if (_revealState == (_pinned, active)) return;
            _revealState = (_pinned, active);

            bool show = !_pinned || active;
            var d = new Duration(TimeSpan.FromMilliseconds(220));

            TitleButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d));
            TitleButtons.IsHitTestVisible = show;
            TitleLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(_pinned && !active ? 0.75 : 1, d));
            foreach (var b in _sectionButtons)
            {
                b.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.8 : 0, d));
                b.IsHitTestVisible = active;
            }
            foreach (var w in _widgets) w.OnPresentationChanged(_pinned, active);
        }

        private void UpdateChrome()
        {
            bool locked = _pinned && _settings.Locked;

            PinButton.Content = _pinned ? "" : "";
            PinButton.ToolTip = _pinned ? "Lift off the desktop (make it a window)" : "Put on desktop";
            PinnedToggle.IsChecked = _pinned;
            ModeGlyph.Text = _pinned ? "" : "";
            AppearanceTitle.Text = _pinned ? "Appearance on the desktop" : "Appearance as a window";

            MinimizeButton.Visibility = _pinned ? Visibility.Collapsed : Visibility.Visible;
            LockButton.Visibility = _pinned ? Visibility.Visible : Visibility.Collapsed;
            LockButton.Content = _settings.Locked ? "" : "";
            LockButton.ToolTip = _settings.Locked ? "Unlock position" : "Lock position";
            LockToggle.IsEnabled = _pinned;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.ResizeBorderThickness = locked ? new Thickness(0) : new Thickness(6);
            TitleBar.Cursor = _pinned && !locked ? Cursors.SizeAll : null;
        }

        private void SyncAppearanceControls()
        {
            _loadingSlider = true;
            OpacitySlider.Value = Math.Round(CurrentOpacity * 100);
            _loadingSlider = false;
            _loadingSlider = true;
            BlurSlider.Value = Math.Round(CurrentBlur * 100);
            _loadingSlider = false;
            OpacityValue.Text = $"{OpacitySlider.Value:0}%";
            BlurValue.Text = BlurSlider.Value == 0 ? "Off" : $"{BlurSlider.Value:0}%";
        }

        // ---------------------------------------------------------------- UI events

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => StartDrag(e);

        /// <summary>Moves the window. Dropping it onto another widget window joins the two.</summary>
        private void StartDrag(MouseButtonEventArgs e)
        {
            if (_pinned && _settings.Locked) return;
            if (e.ClickCount > 1) return;
            _dragging = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Mouse was released before the drag started
            }
            finally
            {
                _dragging = false;
            }
            _manager.EndDrag(this);
        }

        public void ShowDropHint(bool show) => DropHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // ---------------------------------------------------------------- sections

        /// <param name="index">Position from the top, or -1 for the bottom.</param>
        public void AddWidget(IWidget widget, int index = -1)
        {
            if (_widgets.Contains(widget)) return;
            if (index < 0 || index > _widgets.Count) index = _widgets.Count;
            bool hadFill = _widgets.Any(w => w.FillsHeight);
            double current = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 0 : Height);
            _widgets.Insert(index, widget);

            // Grow the window to make room, rather than squeezing what's already there
            if (hadFill && current > 0)
                Height = current + (widget.FillsHeight ? widget.DefaultSize.Height : EstimateHeight(widget));
            else if (!hadFill && widget.FillsHeight && current > 0)
                Height = current + widget.DefaultSize.Height;
            _settings.WidgetIds = _widgets.Select(w => w.Id).ToList();
            BuildSections();
            ApplyLook(animate: false);
        }

        public void RemoveWidget(IWidget widget)
        {
            double removed = EstimateHeight(widget) + (_widgets.Count > 1 ? 38 : 0); // its section and header
            if (!_widgets.Remove(widget)) return;
            _settings.WidgetIds.Remove(widget.Id);
            Detach(widget.CreateView());

            // A list widget left behind keeps its size; shrink back after a compact one leaves
            if (!widget.FillsHeight && _widgets.Any(w => w.FillsHeight) && ActualHeight > 0)
                Height = Math.Max(MinHeight, ActualHeight - removed);
            BuildSections();
        }

        /// <summary>
        /// Lays the widgets out top to bottom. Compact widgets take only the height they need;
        /// list-style widgets share the rest. With more than one widget, each gets a small
        /// header with a button to split it into its own window.
        /// </summary>
        private void BuildSections()
        {
            foreach (var w in _widgets) Detach(w.CreateView());
            Sections.Children.Clear();
            Sections.RowDefinitions.Clear();
            _sectionButtons.Clear();

            bool multi = _widgets.Count > 1;
            for (int i = 0; i < _widgets.Count; i++)
            {
                var widget = _widgets[i];
                Sections.RowDefinitions.Add(new RowDefinition
                {
                    Height = widget.FillsHeight ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
                });

                var section = new DockPanel { LastChildFill = true };
                Grid.SetRow(section, i);
                if (multi)
                {
                    var header = BuildSectionHeader(widget, divider: i > 0);
                    DockPanel.SetDock(header, Dock.Top);
                    section.Children.Add(header);
                }
                section.Children.Add(widget.CreateView());
                Sections.Children.Add(section);
            }

            // In a shared window, the section headers act as titles
            TitleLabel.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
            TitleBar.Height = multi ? 30 : 40;
            TitleText.Text = _widgets.Count == 1 ? _widgets[0].Title : "";
            ModeGlyph.Text = _widgets.Count == 1 ? _widgets[0].Glyph : "\uE8A9";
            Title = "Glasspane – " + DisplayTitle;

            bool anyFill = _widgets.Any(w => w.FillsHeight);
            SizeToContent = anyFill ? SizeToContent.Manual : SizeToContent.Height;
            _revealState = null; // new headers and widgets need their state applied
            UpdateReveal();
        }

        /// <summary>How tall a widget's content is right now (or its default if not shown yet).</summary>
        private static double EstimateHeight(IWidget widget)
        {
            var view = widget.CreateView();
            return view.ActualHeight > 0 ? view.ActualHeight : widget.DefaultSize.Height;
        }

        private FrameworkElement BuildSectionHeader(IWidget widget, bool divider)
        {
            var grid = new Grid
            {
                Height = 32,
                Margin = new Thickness(12, divider ? 6 : 0, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // catches the mouse for dragging
                Cursor = Cursors.SizeAll
            };
            grid.MouseLeftButtonDown += (_, e) => StartDrag(e);

            if (divider)
            {
                grid.Children.Add(new Border
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, -6, 4, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                });
            }

            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), IsHitTestVisible = false };
            label.Children.Add(new TextBlock
            {
                Text = widget.Glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            label.Children.Add(new TextBlock
            {
                Text = widget.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            grid.Children.Add(label);

            var split = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = "\uE8A7",
                ToolTip = $"Split {widget.Title} into its own window",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            split.Click += (_, _) => _manager.Split(this, widget);
            grid.Children.Add(split);
            _sectionButtons.Add(split);

            return grid;
        }

        private static void Detach(FrameworkElement view)
        {
            switch (view.Parent)
            {
                case System.Windows.Controls.Panel panel: panel.Children.Remove(view); break;
                case Decorator decorator: decorator.Child = null; break;
                case ContentControl content: content.Content = null; break;
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

        private void PinnedToggle_Click(object sender, RoutedEventArgs e) => SetPinned(PinnedToggle.IsChecked == true);

        private void LockButton_Click(object sender, RoutedEventArgs e) => SetLocked(!_settings.Locked);

        private void LockToggle_Click(object sender, RoutedEventArgs e) => SetLocked(LockToggle.IsChecked == true);

        private void SetLocked(bool locked)
        {
            _settings.Locked = locked;
            LockToggle.IsChecked = locked;
            UpdateChrome();
            _save();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // A click on the button also closes the popup (StaysOpen=False); don't immediately reopen it
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
            SettingsPopup.IsOpen = true;
            UpdateReveal();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized || OpacityValue == null || _settings == null || _loadingSlider) return;
            CurrentOpacity = OpacitySlider.Value / 100.0;
            OpacityValue.Text = $"{OpacitySlider.Value:0}%";
            ApplyLook(animate: false);
            QueueSave();
        }

        private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized || BlurValue == null || _settings == null || _loadingSlider) return;
            CurrentBlur = BlurSlider.Value / 100.0;
            BlurValue.Text = BlurSlider.Value == 0 ? "Off" : $"{BlurSlider.Value:0}%";
            UpdateBlur();
            QueueSave();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWidget();

        public void HideWidget()
        {
            SettingsPopup.IsOpen = false;
            _settings.Visible = false;
            CapturePlacement();
            _save();
            Hide();
            PowerSaver.Forget(this);
        }

        /// <summary>Lets the app really close the window on exit.</summary>
        public void CloseForExit()
        {
            _allowClose = true;
            CapturePlacement();
            PowerSaver.Forget(this);
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                // Closing (e.g. Alt+F4) just hides; the app keeps running in the tray
                e.Cancel = true;
                HideWidget();
            }
            base.OnClosing(e);
        }
    }
}
