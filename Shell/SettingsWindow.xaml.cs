using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Glasspane.Core;

namespace Glasspane.Shell
{
    /// <summary>One tile in the settings window.</summary>
    public sealed class WidgetTile : INotifyPropertyChanged
    {
        private bool _isOn;

        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public string Glyph { get; init; } = "";
        public bool Available { get; init; } = true;

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) return;
                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
            }
        }

        public string Status => !Available ? "Coming soon" : IsOn ? "On" : "Off";
        public string Hint => !Available ? "Not built yet" : IsOn ? $"Click to hide {Title}" : $"Click to show {Title}";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Opens when Glasspane starts (and from the tray). Pick which widgets show by clicking their tiles.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly WidgetManager _manager;
        private readonly SettingsStore _store;
        private readonly List<WidgetTile> _tiles;
        private bool _allowClose;
        private bool _loadingAppearance;
        private readonly System.Windows.Threading.DispatcherTimer _saveDebounce;

        public SettingsWindow(WidgetManager manager, SettingsStore store)
        {
            InitializeComponent();
            _manager = manager;
            _store = store;

            _tiles = manager.AllWidgets
                .Select(w => new WidgetTile { Id = w.Id, Title = w.Title, Description = w.Description, Glyph = w.Glyph })
                .ToList();
            Tiles.ItemsSource = _tiles;
            RefreshTiles();

            manager.LayoutChanged += (_, _) => RefreshTiles();

            // Dragging a slider changes every widget live; settings are written once you let go
            _saveDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _store.Save(); };

            Activated += (_, _) => { PowerSaver.Report(this, true); SyncToggles(); LoadAppearance(); };
            Deactivated += (_, _) => PowerSaver.Report(this, false);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        }

        /// <summary>Shows the window and brings it to the front.</summary>
        public void Open()
        {
            RefreshTiles();
            SyncToggles();
            LoadAppearance();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void RefreshTiles()
        {
            foreach (var t in _tiles.Where(t => t.Available))
                t.IsOn = _manager.IsShown(t.Id);
        }

        private void SyncToggles()
        {
            try { StartupToggle.IsChecked = StartupManager.IsEnabled; } catch { StartupToggle.IsChecked = false; }
            ShowOnStartToggle.IsChecked = _store.Current.ShowSettingsOnStart;
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WidgetTile { Available: true } tile) return;
            _manager.SetShown(tile.Id, !tile.IsOn);
            RefreshTiles();
            Activate(); // keep this window in front so you can carry on choosing
        }

        // ---------------------------------------------------------------- appearance for all

        private bool DesktopMode => DesktopModeChip.IsChecked == true;

        /// <summary>
        /// Shows the current values. If the widgets differ, the slider sits at their average
        /// and the label says "Mixed" until you move it, which sets them all the same.
        /// </summary>
        private void LoadAppearance()
        {
            if (_manager == null) return;
            var all = _manager.AllWindowSettings.ToList();
            if (all.Count == 0) return;

            var opacities = all.Select(s => DesktopMode ? s.DesktopOpacity : s.BackgroundOpacity).ToList();
            var blurs = all.Select(s => DesktopMode ? s.DesktopBlurStrength : s.BlurStrength).ToList();

            _loadingAppearance = true;
            AllOpacitySlider.Value = Math.Round(opacities.Average() * 100);
            AllBlurSlider.Value = Math.Round(blurs.Average() * 100);
            _loadingAppearance = false;

            AllOpacityValue.Text = Spread(opacities) ? "Mixed" : $"{AllOpacitySlider.Value:0}%";
            AllBlurValue.Text = Spread(blurs) ? "Mixed" : BlurText(AllBlurSlider.Value);
            AppearanceNote.Text = DesktopMode
                ? "Changes every widget's look while it's on the desktop. Each widget's own ⚙ menu can still fine-tune it."
                : "Changes every widget's look while it's a normal window. As a window, blur is simply on or off. Each widget's own ⚙ menu can still fine-tune it.";
        }

        private static bool Spread(List<double> values) => values.Max() - values.Min() > 0.005;

        private static string BlurText(double value) => value == 0 ? "Off" : $"{value:0}%";

        private void AppearanceMode_Checked(object sender, RoutedEventArgs e) => LoadAppearance();

        private void AllOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingAppearance || _manager == null || AllOpacityValue == null) return;
            _manager.SetAppearanceForAll(DesktopMode, opacity: AllOpacitySlider.Value / 100.0, blur: null);
            AllOpacityValue.Text = $"{AllOpacitySlider.Value:0}%";
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void AllBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loadingAppearance || _manager == null || AllBlurValue == null) return;
            _manager.SetAppearanceForAll(DesktopMode, opacity: null, blur: AllBlurSlider.Value / 100.0);
            AllBlurValue.Text = BlurText(AllBlurSlider.Value);
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartupManager.SetEnabled(StartupToggle.IsChecked == true);
            }
            catch (Exception ex)
            {
                Log.Write("Startup toggle failed: " + ex.Message);
                SyncToggles();
            }
        }

        private void ShowOnStartToggle_Click(object sender, RoutedEventArgs e)
        {
            _store.Current.ShowSettingsOnStart = ShowOnStartToggle.IsChecked == true;
            _store.Save();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        public void CloseForExit()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true; // Alt+F4 just hides it
                Hide();
            }
            PowerSaver.Forget(this);
            base.OnClosing(e);
        }
    }
}
