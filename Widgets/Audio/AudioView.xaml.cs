using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Glasspane.Widgets.Audio
{
    public partial class AudioView : UserControl
    {
        private readonly AudioService _audio;
        private readonly DispatcherTimer _meterTimer;
        private readonly DispatcherTimer _appsTimer;
        private bool _updatingSlider;
        private bool _appsOpen;
        private bool _loadingApps;
        private double _level;
        private bool _active;

        public AudioView(AudioService audio)
        {
            _audio = audio;
            InitializeComponent();

            // Level meter and app list refresh only run while you're actually using the widget
            // (mouse over it or it has focus). The rest of the time the audio widget does no work
            // at all: volume and device changes still arrive instantly as Windows notifications.
            _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _meterTimer.Tick += (_, _) => UpdateMeter();

            _appsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _appsTimer.Tick += (_, _) => RefreshApps();

            IsVisibleChanged += (_, _) => UpdateTimers();

            _audio.VolumeChanged += RefreshVolume;
            _audio.DevicesChanged += () =>
            {
                RefreshDevices();
                if (_appsOpen) RefreshApps();
            };

            // Scroll anywhere over the widget to change the volume
            PreviewMouseWheel += OnMouseWheel;

            RefreshDevices();
            RefreshVolume();
        }

        // ---------------------------------------------------------------- volume

        private void RefreshVolume()
        {
            double volume = Math.Round(_audio.Volume);
            bool muted = _audio.Muted;

            _updatingSlider = true;
            VolumeSlider.Value = volume;
            _updatingSlider = false;

            PercentText.Text = volume.ToString("0");
            PercentText.Opacity = muted ? 0.4 : 1;
            VolumeSlider.Opacity = muted ? 0.5 : 1;
            MuteButton.Content = GlyphFor(volume, muted);
            MuteButton.ToolTip = muted ? "Unmute" : "Mute";
            DeviceName.Text = _audio.HasDevice ? Shorten(_audio.DeviceName) : "No output device";
            DeviceDetail.Text = _audio.HasDevice ? (muted ? "Muted" : _audio.DeviceName) : "Plug in speakers or headphones";
        }

        private static string Shorten(string full)
        {
            int open = full.IndexOf(" (", StringComparison.Ordinal);
            return open > 0 && full.EndsWith(")") ? full.Substring(open + 2, full.Length - open - 3) : full;
        }

        private static string GlyphFor(double volume, bool muted)
        {
            if (muted || volume <= 0) return "";
            if (volume < 34) return "";
            if (volume < 67) return "";
            return "";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSlider || PercentText == null) return;
            _audio.Volume = e.NewValue;
            if (_audio.Muted && e.NewValue > e.OldValue) _audio.Muted = false; // turning it up unmutes, like Windows
            PercentText.Text = Math.Round(e.NewValue).ToString("0");
            MuteButton.Content = GlyphFor(e.NewValue, _audio.Muted);
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            _audio.Muted = !_audio.Muted;
            RefreshVolume();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Leave the wheel alone over the app list's own sliders
            if (AppList.IsMouseOver) return;
            double step = e.Delta > 0 ? 2 : -2;
            _audio.Volume = Math.Clamp(Math.Round(_audio.Volume) + step, 0, 100);
            if (step > 0 && _audio.Muted) _audio.Muted = false;
            RefreshVolume();
            e.Handled = true;
        }

        private void UpdateMeter()
        {
            double peak = _audio.Muted ? 0 : _audio.Peak;
            _level = Math.Max(peak, _level * 0.82); // fast attack, gentle fall
            if (_level < 0.002) _level = 0;
            MeterFill.Width = Math.Max(0, MeterTrack.ActualWidth * Math.Min(1, _level));
        }

        // ---------------------------------------------------------------- devices

        private void RefreshDevices()
        {
            DeviceList.ItemsSource = _audio.GetOutputs();
        }

        private void Device_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not OutputDevice device || device.IsDefault) return;
            _audio.SetDefault(device.Id);
            // Windows sends a "default device changed" notification, which refreshes everything
        }

        // ---------------------------------------------------------------- apps

        private void AppsToggle_Click(object sender, RoutedEventArgs e)
        {
            _appsOpen = !_appsOpen;
            AppList.Visibility = _appsOpen ? Visibility.Visible : Visibility.Collapsed;
            AppsChevron.Text = _appsOpen ? "" : "";
            if (_appsOpen) RefreshApps();
            else NoApps.Visibility = Visibility.Collapsed;
            UpdateTimers();
        }

        private async void RefreshApps()
        {
            // Don't swap the list out while someone is dragging one of its sliders
            if (_loadingApps || (AppList.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)) return;
            _loadingApps = true;
            try
            {
                List<AppVolume> apps = await _audio.GetAppsAsync();
                if (!_appsOpen) return;
                if (AppList.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed) return;

                AppList.ItemsSource = apps;
                NoApps.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _loadingApps = false;
            }
        }

        private void AppMute_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AppVolume app) app.Muted = !app.Muted;
        }

        // ---------------------------------------------------------------- desktop presentation

        /// <summary>On the desktop, the app-volume link fades out when you're not using the widget.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            bool wasActive = _active;
            _active = active;
            bool show = !onDesktop || active || _appsOpen;
            AppsToggle.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(220)));
            AppsToggle.IsHitTestVisible = show;

            if (active && !wasActive && _appsOpen) RefreshApps(); // catch up on anything that changed meanwhile
            UpdateTimers();
        }

        private void UpdateTimers()
        {
            bool run = IsVisible && _active;
            if (run) _meterTimer.Start();
            else
            {
                _meterTimer.Stop();
                _level = 0;
                MeterFill.Width = 0;
            }

            if (run && _appsOpen) _appsTimer.Start();
            else _appsTimer.Stop();
        }

        public void Detach()
        {
            _meterTimer.Stop();
            _appsTimer.Stop();
            _audio.VolumeChanged -= RefreshVolume;
        }
    }
}
