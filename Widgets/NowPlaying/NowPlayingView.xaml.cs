using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glasspane.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Glasspane.Widgets.NowPlaying
{
    /// <summary>
    /// Shows whatever is playing (Spotify, YouTube in a browser, Windows Media Player…) using the
    /// same media-control system as the volume pop-up in Windows. Windows notifies the widget when
    /// the track or play state changes; the only timer is the progress bar, which runs only while
    /// something is playing and the widget is visible.
    /// </summary>
    public partial class NowPlayingView : UserControl
    {
        private readonly DispatcherTimer _progressTimer;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private TimeSpan _position, _duration;
        private DateTimeOffset _positionAt;
        private bool _playing;
        private int _artVersion;
        private string _trackKey = "";

        public NowPlayingView()
        {
            InitializeComponent();
            _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _progressTimer.Tick += (_, _) => UpdateProgress();
            IsVisibleChanged += (_, _) => UpdateProgressTimer();
            ProgressTrack.SizeChanged += (_, _) => UpdateProgress();
            ShowIdle();
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (_, _) => Post(AttachCurrent);
                _manager.SessionsChanged += (_, _) => Post(AttachCurrent);
                AttachCurrent();
            }
            catch (Exception ex)
            {
                Log.Write("Media controls unavailable: " + ex.Message);
                ShowIdle();
            }
        }

        // Windows raises media events on background threads
        private void Post(Action action) => Dispatcher.InvokeAsync(action);

        private void AttachCurrent()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            try
            {
                _session = _manager?.GetCurrentSession();
            }
            catch
            {
                _session = null;
            }

            if (_session == null)
            {
                ShowIdle();
                return;
            }

            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;

            _ = RefreshMediaAsync();
            RefreshPlayback();
            RefreshTimeline();
        }

        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
            Post(() => { _ = RefreshMediaAsync(); });

        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
            Post(RefreshPlayback);

        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
            Post(RefreshTimeline);

        // ---------------------------------------------------------------- reading state

        private async Task RefreshMediaAsync()
        {
            var session = _session;
            if (session == null) return;

            GlobalSystemMediaTransportControlsSessionMediaProperties props;
            try
            {
                props = await session.TryGetMediaPropertiesAsync();
            }
            catch
            {
                return;
            }
            if (session != _session || props == null) return;

            IdleState.Visibility = Visibility.Collapsed;
            PlayingState.Visibility = Visibility.Visible;
            // A different track: forget the old length so a new one is picked up
            string key = props.Title + "\u0001" + props.Artist + "\u0001" + session.SourceAppUserModelId;
            if (key != _trackKey)
            {
                _trackKey = key;
                _duration = TimeSpan.Zero;
                RefreshTimeline();
            }

            TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            string artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : props.AlbumArtist;
            ArtistText.Text = artist ?? "";
            ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
            SourceText.Text = FriendlySource(session.SourceAppUserModelId);

            // Album art: the newest request wins if tracks change quickly
            int version = ++_artVersion;
            var art = await LoadArtAsync(props.Thumbnail);
            if (version == _artVersion) ArtImage.Background = art;
        }

        private static async Task<Brush?> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
        {
            if (thumbnail == null) return null;
            try
            {
                using var source = await thumbnail.OpenReadAsync();
                using var stream = source.AsStreamForRead();
                var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                memory.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = memory;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 160; // shown at 76 px; small decode keeps memory tiny
                image.EndInit();
                image.Freeze();

                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                return brush;
            }
            catch
            {
                return null;
            }
        }

        private void RefreshPlayback()
        {
            var session = _session;
            if (session == null) return;
            try
            {
                var info = session.GetPlaybackInfo();
                _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                PlayPauseButton.Content = _playing ? "" : "";
                PlayPauseButton.ToolTip = _playing ? "Pause" : "Play";
                PreviousButton.IsEnabled = info.Controls.IsPreviousEnabled;
                NextButton.IsEnabled = info.Controls.IsNextEnabled;
                PlayPauseButton.IsEnabled = info.Controls.IsPlayPauseToggleEnabled || info.Controls.IsPlayEnabled || info.Controls.IsPauseEnabled;
            }
            catch
            {
                // session ended
            }
            UpdateProgressTimer();
            UpdateProgress();
        }

        private void RefreshTimeline()
        {
            var session = _session;
            if (session == null) return;
            try
            {
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;

                // While seeking, some apps (Firefox in particular) briefly report a length of zero and
                // then don't send another update until play/pause. Keep the length we already know
                // for this track instead of hiding the bar.
                if (duration <= TimeSpan.Zero)
                {
                    if (_duration > TimeSpan.Zero) { UpdateProgress(); return; }
                }
                else
                {
                    _duration = duration;
                    var position = timeline.Position - timeline.StartTime;
                    if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                    if (position > duration) position = duration;
                    _position = position;
                    _positionAt = timeline.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : timeline.LastUpdatedTime;
                }
            }
            catch
            {
                // Couldn't read it this time: keep what we had
            }
            ProgressRow.Visibility = _duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgressTimer();
            UpdateProgress();
        }

        // ---------------------------------------------------------------- progress

        private TimeSpan CurrentPosition()
        {
            var position = _position;
            if (_playing) position += DateTimeOffset.Now - _positionAt;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _duration) position = _duration;
            return position;
        }

        private void UpdateProgress()
        {
            if (_duration <= TimeSpan.Zero) return;
            var position = CurrentPosition();
            ProgressFill.Width = ProgressTrack.ActualWidth * (position.TotalSeconds / _duration.TotalSeconds);
            PositionText.Text = Format(position);
            DurationText.Text = Format(_duration);
        }

        private void UpdateProgressTimer()
        {
            bool run = IsVisible && _playing && _duration > TimeSpan.Zero;
            if (run && !_progressTimer.IsEnabled) _progressTimer.Start();
            else if (!run) _progressTimer.Stop();
        }

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        private void ShowIdle()
        {
            IdleState.Visibility = Visibility.Visible;
            PlayingState.Visibility = Visibility.Collapsed;
            _playing = false;
            _duration = TimeSpan.Zero;
            _trackKey = "";
            ArtImage.Background = null;
            UpdateProgressTimer();
        }

        /// <summary>Turns Windows' app ids into names: "Spotify.exe" → "Spotify".</summary>
        private static string FriendlySource(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string lower = id.ToLowerInvariant();
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("firefox") || lower == "308046b0af4a39cb") return "Firefox";
            if (lower.Contains("chrome")) return "Chrome";
            if (lower.Contains("msedge")) return "Edge";
            if (lower.Contains("brave")) return "Brave";
            if (lower.Contains("zunemusic") || lower.Contains("media.player")) return "Media Player";
            if (lower.Contains("vlc")) return "VLC";
            string name = id;
            int bang = name.LastIndexOf('!');
            if (bang >= 0) name = name.Substring(bang + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name;
        }

        // ---------------------------------------------------------------- controls

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
        }

        private async void Previous_Click(object sender, RoutedEventArgs e)
        {
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
        }

        /// <summary>Click the progress bar to jump to that point (if the app allows it).</summary>
        private async void ProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_session == null || _duration <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;
            double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
            var target = TimeSpan.FromSeconds(_duration.TotalSeconds * ratio);
            try
            {
                if (await _session.TryChangePlaybackPositionAsync(target.Ticks))
                {
                    _position = target;
                    _positionAt = DateTimeOffset.Now;
                    UpdateProgress();
                }
            }
            catch
            {
                // app doesn't support seeking
            }
        }

        public void Detach()
        {
            _progressTimer.Stop();
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
        }
    }
}
