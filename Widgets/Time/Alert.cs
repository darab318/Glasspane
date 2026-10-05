using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Time
{
    /// <summary>
    /// The pop-up for an alarm or finished timer: stays on top of everything, plays the Windows
    /// alarm sound on repeat, and offers Snooze (alarms) and Dismiss. Stops by itself after 5 minutes.
    /// </summary>
    internal sealed class Alert : Window
    {
        private static int _open;
        private readonly SoundPlayer? _sound;
        private readonly DispatcherTimer _autoStop = new() { Interval = TimeSpan.FromMinutes(5) };

        public static void Raise(string title, string subtitle, string detail, bool canSnooze, Action? snooze)
        {
            try
            {
                new Alert(title, subtitle, detail, canSnooze, snooze).Show();
            }
            catch (Exception ex)
            {
                Log.Write("Alert failed: " + ex);
            }
        }

        private Alert(string title, string subtitle, string detail, bool canSnooze, Action? snooze)
        {
            Title = "Glasspane – " + title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Foreground = (Brush)FindResource("TextBrush");

            var panel = new StackPanel { Width = 300 };
            panel.Children.Add(new TextBlock
            {
                Text = title.StartsWith("Timer") ? "" : "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 28,
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = (Brush)FindResource("SubtleTextBrush") });
            panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = detail, FontSize = 13, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 18) });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (canSnooze && snooze != null)
            {
                var snoozeButton = new Button { Content = "Snooze 5 min", Style = (Style)FindResource("PillButton"), Margin = new Thickness(0, 0, 8, 0) };
                snoozeButton.Click += (_, _) => { snooze(); Close(); };
                buttons.Children.Add(snoozeButton);
            }
            var dismiss = new Button { Content = "Dismiss", Style = (Style)FindResource("AccentPill"), Padding = new Thickness(18, 6, 18, 6) };
            dismiss.Click += (_, _) => Close();
            buttons.Children.Add(dismiss);
            panel.Children.Add(buttons);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x1A, 0x22)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(22, 18, 22, 18),
                Child = panel
            };

            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
            KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) Close(); };

            // Bottom-right of the main screen, stacked if several are open
            Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 24;
                Top = area.Bottom - ActualHeight - 24 - _open * (ActualHeight + 12);
                _open++;
            };
            Closed += (_, _) =>
            {
                _open = Math.Max(0, _open - 1);
                _sound?.Stop();
                _sound?.Dispose();
                _autoStop.Stop();
                PowerSaver.Report(this, false);
            };

            _sound = LoadSound();
            try { _sound?.PlayLooping(); } catch { SystemSounds.Exclamation.Play(); }
            if (_sound == null) SystemSounds.Exclamation.Play();

            _autoStop.Tick += (_, _) => Close();
            _autoStop.Start();
            PowerSaver.Report(this, true);
        }

        /// <summary>Windows' own alarm sound.</summary>
        private static SoundPlayer? LoadSound()
        {
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            var file = new[] { "Alarm01.wav", "Alarm02.wav", "Alarm03.wav", "Windows Notify System Generic.wav" }
                .Select(f => Path.Combine(media, f))
                .FirstOrDefault(File.Exists);
            return file == null ? null : new SoundPlayer(file);
        }
    }
}
