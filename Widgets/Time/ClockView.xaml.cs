using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Glasspane.Widgets.Time
{
    public partial class ClockView : UserControl
    {
        private static readonly string[] DayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        private readonly ClockService _clock;
        private readonly DispatcherTimer _clockTick = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _fastTick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly ToggleButton[] _days = new ToggleButton[7];
        private DateTime _popupClosedAt;
        private bool _onDesktop, _active;

        public ClockView(ClockService clock)
        {
            _clock = clock;
            InitializeComponent();

            // The clock only redraws once a minute (or once a second with seconds on), and only while visible
            _clockTick.Tick += (_, _) => { UpdateClock(); ScheduleClockTick(); };
            // The timer/stopwatch display updates 10 times a second, only while that section is open and running
            _fastTick.Tick += (_, _) => UpdateFast();

            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    UpdateClock();
                    ScheduleClockTick();
                    UpdateTimerUi();
                    UpdateStopwatchUi();
                }
                else
                {
                    _clockTick.Stop();
                }
                UpdateFastTimer();
            };

            for (int i = 0; i < 7; i++)
            {
                _days[i] = new ToggleButton
                {
                    Style = (Style)FindResource("DayChip"),
                    Content = DayNames[i].Substring(0, 2),
                    ToolTip = DayNames[i]
                };
                DayToggles.Children.Add(_days[i]);
            }

            foreach (var (label, minutes) in new[] { ("1 min", 1), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("1 hour", 60) })
            {
                var button = new Button { Style = (Style)FindResource("PillButton"), Content = label, Margin = new Thickness(3) };
                button.Click += (_, _) =>
                {
                    TimerError.Visibility = Visibility.Collapsed;
                    _clock.StartTimer(TimeSpan.FromMinutes(minutes));
                };
                TimerPresets.Children.Add(button);
            }

            _clock.AlarmsChanged += () => { RefreshAlarms(); UpdateStatus(); };
            _clock.TimerChanged += () => { UpdateTimerUi(); UpdateStatus(); UpdateFastTimer(); };
            TimerTrack.SizeChanged += (_, _) => UpdateTimerUi();
            OptionsPopup.Closed += (_, _) => _popupClosedAt = DateTime.UtcNow;

            SecondsToggle.IsChecked = _clock.Data.ShowSeconds;
            Hour24Toggle.IsChecked = _clock.Data.Use24Hour;

            RefreshAlarms();
            UpdateClock();
            UpdateTimerUi();
            UpdateStopwatchUi();
        }

        // ---------------------------------------------------------------- clock

        private void UpdateClock()
        {
            var now = DateTime.Now;
            if (_clock.Data.Use24Hour)
            {
                TimeText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
                AmPmText.Visibility = Visibility.Collapsed;
            }
            else
            {
                TimeText.Text = now.ToString("h:mm", CultureInfo.InvariantCulture);
                AmPmText.Text = now.Hour < 12 ? "am" : "pm";
                AmPmText.Visibility = Visibility.Visible;
            }
            SecondsText.Text = now.ToString("ss", CultureInfo.InvariantCulture);
            SecondsText.Visibility = _clock.Data.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
            DateText.Text = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            UpdateStatus();
        }

        private void ScheduleClockTick()
        {
            _clockTick.Stop();
            if (!IsVisible) return;
            var now = DateTime.Now;
            var next = _clock.Data.ShowSeconds
                ? now.AddMilliseconds(1000 - now.Millisecond)
                : now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
            _clockTick.Interval = next - now + TimeSpan.FromMilliseconds(15);
            _clockTick.Start();
        }

        /// <summary>One line under the date: next alarm, running timer, running stopwatch.</summary>
        private void UpdateStatus()
        {
            var parts = new System.Collections.Generic.List<string>();
            var now = DateTime.Now;

            if (_clock.NextAlarm() is DateTime alarm)
            {
                string day = alarm.Date == now.Date ? "" : alarm.Date == now.Date.AddDays(1) ? "tomorrow " : alarm.ToString("ddd ", CultureInfo.CurrentCulture);
                parts.Add($"Next alarm {day}{ClockFormat.Time(alarm.Hour, alarm.Minute)}");
            }
            if (_clock.TimerEndsAt is DateTime end)
                parts.Add($"Timer ends {ClockFormat.Time(end.Hour, end.Minute)}");
            else if (_clock.TimerPaused)
                parts.Add("Timer paused");
            if (_clock.Stopwatch.IsRunning)
                parts.Add("Stopwatch running");

            StatusText.Text = string.Join("  ·  ", parts);
            StatusText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- sections

        private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Clicking the open section's chip closes it again, back to just the clock
            if (sender is RadioButton { IsChecked: true } tab)
            {
                tab.IsChecked = false;
                ShowSection(null);
                e.Handled = true;
            }
        }

        private void Tab_Checked(object sender, RoutedEventArgs e) => ShowSection(sender as RadioButton);

        private void ShowSection(RadioButton? tab)
        {
            AlarmsPanel.Visibility = tab == AlarmsTab ? Visibility.Visible : Visibility.Collapsed;
            TimerPanel.Visibility = tab == TimerTab ? Visibility.Visible : Visibility.Collapsed;
            StopwatchPanel.Visibility = tab == StopwatchTab ? Visibility.Visible : Visibility.Collapsed;
            if (tab == TimerTab) UpdateTimerUi();
            if (tab == StopwatchTab) UpdateStopwatchUi();
            UpdateFastTimer();
            ApplyPresentation();
        }

        private bool AnySectionOpen =>
            AlarmsPanel.Visibility == Visibility.Visible || TimerPanel.Visibility == Visibility.Visible ||
            StopwatchPanel.Visibility == Visibility.Visible;

        private void UpdateFastTimer()
        {
            bool run = IsVisible &&
                       ((TimerPanel.Visibility == Visibility.Visible && _clock.TimerRunning) ||
                        (StopwatchPanel.Visibility == Visibility.Visible && _clock.Stopwatch.IsRunning));
            if (run && !_fastTick.IsEnabled) _fastTick.Start();
            else if (!run) _fastTick.Stop();
        }

        private void UpdateFast()
        {
            if (TimerPanel.Visibility == Visibility.Visible) UpdateTimerUi();
            if (StopwatchPanel.Visibility == Visibility.Visible)
                StopwatchDisplay.Text = ClockFormat.Duration(_clock.Stopwatch.Elapsed, tenths: true);
        }

        // ---------------------------------------------------------------- alarms

        private void RefreshAlarms()
        {
            foreach (var a in _clock.Data.Alarms) a.RefreshText();
            AlarmList.ItemsSource = null;
            AlarmList.ItemsSource = _clock.Data.Alarms.ToList();
            NoAlarms.Visibility = _clock.Data.Alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AlarmToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { DataContext: Alarm alarm } box)
            {
                alarm.Enabled = box.IsChecked == true;
                _clock.AlarmsUpdated();
            }
        }

        private void AlarmDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Alarm alarm) _clock.RemoveAlarm(alarm);
        }

        private void NewAlarm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddAlarm();
                e.Handled = true;
            }
        }

        private void AddAlarm_Click(object sender, RoutedEventArgs e) => AddAlarm();

        private void AddAlarm()
        {
            if (!TryParseTime(NewAlarmTime.Text, out int hour, out int minute))
            {
                ShowAlarmMessage("Enter a time like 07:30, 7.30 or 7:30pm", error: true);
                NewAlarmTime.Focus();
                return;
            }

            int days = 0;
            for (int i = 0; i < 7; i++)
                if (_days[i].IsChecked == true) days |= 1 << i;

            var alarm = new Alarm { Hour = hour, Minute = minute, Label = NewAlarmLabel.Text.Trim(), Days = days };
            _clock.AddAlarm(alarm);

            NewAlarmTime.Clear();
            NewAlarmLabel.Clear();
            foreach (var d in _days) d.IsChecked = false;

            if (alarm.NextAfter(DateTime.Now) is DateTime next)
            {
                var wait = next - DateTime.Now;
                string inText = wait.TotalHours >= 1 ? $"{(int)wait.TotalHours} h {wait.Minutes} min" : $"{Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} min";
                ShowAlarmMessage($"Alarm set for {alarm.TimeText}, in {inText}", error: false);
            }
        }

        private void ShowAlarmMessage(string text, bool error)
        {
            AlarmError.Text = text;
            AlarmError.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B)) : (Brush)FindResource("SubtleTextBrush");
        }

        /// <summary>Accepts 7:30, 07:30, 7.30, 730, 19:45, 7pm, 7:30 pm.</summary>
        private static bool TryParseTime(string input, out int hour, out int minute)
        {
            hour = minute = 0;
            string s = input.Trim().ToLowerInvariant().Replace(" ", "");
            if (s.Length == 0) return false;

            bool pm = s.EndsWith("pm"), am = s.EndsWith("am");
            if (pm || am) s = s.Substring(0, s.Length - 2);

            var m = Regex.Match(s, @"^(\d{1,2})(?:[:.](\d{2}))?$");
            if (!m.Success)
            {
                m = Regex.Match(s, @"^(\d{1,2})(\d{2})$"); // 730, 1945
                if (!m.Success) return false;
            }

            hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (am || pm)
            {
                if (hour < 1 || hour > 12) return false;
                if (hour == 12) hour = 0;
                if (pm) hour += 12;
            }
            return hour <= 23 && minute <= 59;
        }

        // ---------------------------------------------------------------- timer

        private void UpdateTimerUi()
        {
            var remaining = _clock.TimerRemaining;
            bool idle = !_clock.TimerRunning && !_clock.TimerPaused;
            TimerDisplay.Text = ClockFormat.Duration(idle ? TimeSpan.Zero : remaining);
            TimerDisplay.Opacity = _clock.TimerPaused ? 0.6 : 1;

            double fraction = _clock.TimerTotal > TimeSpan.Zero && !idle ? remaining.TotalSeconds / _clock.TimerTotal.TotalSeconds : 0;
            TimerFill.Width = TimerTrack.ActualWidth * Math.Clamp(fraction, 0, 1);

            TimerStartButton.Content = _clock.TimerRunning ? "Pause" : _clock.TimerPaused ? "Resume" : "Start";
            TimerResetButton.IsEnabled = !idle;
        }

        private void TimerStart_Click(object sender, RoutedEventArgs e) => StartOrPauseTimer();

        private void TimerInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!_clock.TimerRunning && !_clock.TimerPaused) StartOrPauseTimer();
            e.Handled = true;
        }

        private void StartOrPauseTimer()
        {
            TimerError.Visibility = Visibility.Collapsed;
            if (_clock.TimerRunning) { _clock.PauseTimer(); return; }
            if (_clock.TimerPaused) { _clock.ResumeTimer(); return; }

            if (TryParseDuration(TimerInput.Text, out var duration))
            {
                _clock.StartTimer(duration);
                TimerInput.Clear();
            }
            else
            {
                TimerError.Text = "Enter minutes (e.g. 25), or m:ss / h:mm:ss";
                TimerError.Visibility = Visibility.Visible;
                TimerInput.Focus();
            }
        }

        private void TimerReset_Click(object sender, RoutedEventArgs e) => _clock.ResetTimer();

        /// <summary>"25" = 25 minutes, "1:30" = 1 min 30 s, "1:00:00" = 1 hour, also "90s", "2h", "45m".</summary>
        private static bool TryParseDuration(string input, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            string s = input.Trim().ToLowerInvariant().Replace(" ", "");
            if (s.Length == 0) return false;

            var unit = Regex.Match(s, @"^(\d+(?:\.\d+)?)(h|hr|hrs|hour|hours|m|min|mins|minutes|s|sec|secs|seconds)$");
            if (unit.Success)
            {
                double n = double.Parse(unit.Groups[1].Value, CultureInfo.InvariantCulture);
                duration = unit.Groups[2].Value[0] switch
                {
                    'h' => TimeSpan.FromHours(n),
                    'm' => TimeSpan.FromMinutes(n),
                    _ => TimeSpan.FromSeconds(n)
                };
            }
            else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes))
            {
                duration = TimeSpan.FromMinutes(minutes);
            }
            else
            {
                var parts = s.Split(':');
                if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, out _))) return false;
                var n = parts.Select(int.Parse).ToArray();
                duration = parts.Length == 2
                    ? new TimeSpan(0, n[0], n[1])
                    : new TimeSpan(n[0], n[1], n[2]);
            }
            return duration > TimeSpan.Zero && duration < TimeSpan.FromDays(1);
        }

        // ---------------------------------------------------------------- stopwatch

        private void StopwatchStart_Click(object sender, RoutedEventArgs e)
        {
            if (_clock.Stopwatch.IsRunning) _clock.Stopwatch.Stop();
            else _clock.Stopwatch.Start();
            UpdateStopwatchUi();
            UpdateStatus();
        }

        private void StopwatchLap_Click(object sender, RoutedEventArgs e)
        {
            if (!_clock.Stopwatch.IsRunning) return;
            _clock.Laps.Add(_clock.Stopwatch.Elapsed);
            UpdateStopwatchUi();
        }

        private void StopwatchReset_Click(object sender, RoutedEventArgs e)
        {
            _clock.Stopwatch.Reset();
            _clock.Laps.Clear();
            UpdateStopwatchUi();
            UpdateStatus();
        }

        private void UpdateStopwatchUi()
        {
            var sw = _clock.Stopwatch;
            StopwatchDisplay.Text = ClockFormat.Duration(sw.Elapsed, tenths: true);
            StopwatchStartButton.Content = sw.IsRunning ? "Stop" : sw.Elapsed > TimeSpan.Zero ? "Resume" : "Start";
            StopwatchLapButton.IsEnabled = sw.IsRunning;

            // Laps, newest first: lap time and total
            LapList.Children.Clear();
            var subtle = (Brush)FindResource("SubtleTextBrush");
            for (int i = _clock.Laps.Count - 1; i >= 0 && i >= _clock.Laps.Count - 10; i--)
            {
                var lap = _clock.Laps[i] - (i > 0 ? _clock.Laps[i - 1] : TimeSpan.Zero);
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.Children.Add(new TextBlock { Text = $"Lap {i + 1}", FontSize = 12, Foreground = subtle });
                var lapText = new TextBlock { Text = ClockFormat.Duration(lap, tenths: true), FontSize = 12 };
                Grid.SetColumn(lapText, 1);
                row.Children.Add(lapText);
                var totalText = new TextBlock { Text = ClockFormat.Duration(_clock.Laps[i], tenths: true), FontSize = 12, Foreground = subtle, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(totalText, 2);
                row.Children.Add(totalText);
                LapList.Children.Add(row);
            }
            UpdateFastTimer();
        }

        // ---------------------------------------------------------------- options and presentation

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
            OptionsPopup.IsOpen = true;
        }

        private void ClockOption_Click(object sender, RoutedEventArgs e)
        {
            _clock.Data.ShowSeconds = SecondsToggle.IsChecked == true;
            _clock.Data.Use24Hour = Hour24Toggle.IsChecked == true;
            ClockFormat.Use24Hour = _clock.Data.Use24Hour;
            _clock.Save();
            RefreshAlarms();
            UpdateClock();
            ScheduleClockTick();
        }

        /// <summary>On the desktop the chips and options only show while you're using the clock.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            _onDesktop = onDesktop;
            _active = active;
            ApplyPresentation();
        }

        private void ApplyPresentation()
        {
            bool show = !_onDesktop || _active || AnySectionOpen;
            var d = TimeSpan.FromMilliseconds(220);
            TabRow.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d));
            TabRow.IsHitTestVisible = show;
            OptionsButton.BeginAnimation(OpacityProperty, new DoubleAnimation(!_onDesktop || _active ? 1 : 0, d));
            OptionsButton.IsHitTestVisible = !_onDesktop || _active;
        }

        public void Stop()
        {
            _clockTick.Stop();
            _fastTick.Stop();
        }
    }
}
