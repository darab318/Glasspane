using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Weather
{
    /// <summary>
    /// Current weather, the next hours and the next 5 days. Updates every 30 minutes while the
    /// widget is visible, and never while it's hidden.
    /// </summary>
    public partial class WeatherView : UserControl
    {
        private static readonly TimeSpan UpdateEvery = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

        private readonly WeatherService _service;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
        private Forecast? _forecast;
        private bool _fetching;
        private bool _loadingOptions;
        private DateTime _popupClosedAt;

        public WeatherView(WeatherService service)
        {
            _service = service;
            InitializeComponent();

            _timer.Tick += (_, _) => _ = Update();
            IsVisibleChanged += (_, _) =>
            {
                if (!IsVisible)
                {
                    _timer.Stop();
                    return;
                }
                // Coming into view: update if the forecast is old, otherwise wait out the rest of the 30 minutes
                var age = _forecast == null ? UpdateEvery : DateTime.Now - _forecast.FetchedAt;
                if (age >= UpdateEvery) _ = Update();
                else Schedule(UpdateEvery - age);
            };
            OptionsPopup.Closed += (_, _) => _popupClosedAt = DateTime.UtcNow;

            LoadOptionsIntoSwitches();
            _forecast = _service.LoadCached();
            Show();
        }

        private void Schedule(TimeSpan wait)
        {
            _timer.Stop();
            if (!IsVisible) return;
            _timer.Interval = wait < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait;
            _timer.Start();
        }

        private async Task Update()
        {
            if (_fetching) return;
            _fetching = true;
            _timer.Stop();
            StatusText.Text = "Updating…";
            try
            {
                _forecast = await _service.FetchAsync();
                Show();
                Schedule(UpdateEvery);
            }
            catch (Exception ex)
            {
                Log.Write("Weather update failed: " + ex.Message);
                StatusText.Text = _forecast == null
                    ? "Can't reach the weather service. Trying again in 5 minutes."
                    : $"Offline: showing the forecast from {_forecast.FetchedAt:HH:mm}. Retrying soon.";
                Schedule(RetryAfter);
            }
            finally
            {
                _fetching = false;
            }
        }

        // ---------------------------------------------------------------- display

        private string Deg(double t) => $"{Math.Round(t):0}°";

        private void Show()
        {
            var o = _service.Options;
            PlaceText.Text = o.Place;
            Hourly.Visibility = o.ShowHourly ? Visibility.Visible : Visibility.Collapsed;
            Daily.Visibility = o.ShowDaily ? Visibility.Visible : Visibility.Collapsed;

            var f = _forecast;
            if (f == null)
            {
                NowSymbol.Text = "";
                NowTemp.Text = "–";
                NowText.Text = "Loading weather…";
                NowHighLow.Text = "";
                NowDetail.Text = "";
                Hourly.Children.Clear();
                Daily.Children.Clear();
                return;
            }

            NowSymbol.Text = WeatherService.Symbol(f.Code, f.IsDay);
            NowTemp.Text = Deg(f.Temperature);
            NowText.Text = WeatherService.Describe(f.Code);
            var today = f.Days.FirstOrDefault();
            NowHighLow.Text = today == null ? "" : $"H {Deg(today.High)}   L {Deg(today.Low)}";

            string wind = o.WindKmh ? "km/h" : "mph";
            var detail = $"Feels like {Deg(f.FeelsLike)}  ·  Wind {Math.Round(f.Wind):0} {wind}  ·  Humidity {f.Humidity}%";
            if (f.Sunrise is DateTime sunrise && f.Sunset is DateTime sunset)
                detail += DateTime.Now < sunrise ? $"  ·  Sunrise {sunrise:HH:mm}" : DateTime.Now < sunset ? $"  ·  Sunset {sunset:HH:mm}" : "";
            NowDetail.Text = detail;

            BuildHourly(f);
            BuildDaily(f);
            StatusText.Text = $"Updated {f.FetchedAt:HH:mm}";
        }

        /// <summary>The next 6 hours, starting with the current one.</summary>
        private void BuildHourly(Forecast f)
        {
            Hourly.Children.Clear();
            var now = DateTime.Now;
            var hours = f.Hours.Where(h => h.Time > now.AddHours(-1)).Take(6).ToList();
            var subtle = (Brush)FindResource("SubtleTextBrush");
            foreach (var h in hours)
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                col.Children.Add(new TextBlock { Text = h.Time.Hour == now.Hour && h.Time.Date == now.Date ? "Now" : h.Time.ToString("HH:mm"), FontSize = 11, Foreground = subtle, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock { Text = WeatherService.Symbol(h.Code, h.IsDay), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 1) });
                col.Children.Add(new TextBlock { Text = Deg(h.Temperature), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock { Text = h.RainChance >= 10 ? $"{h.RainChance}%" : " ", FontSize = 10, Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center });
                Hourly.Children.Add(col);
            }
        }

        /// <summary>The next 5 days, with a bar showing each day's range against the week's.</summary>
        private void BuildDaily(Forecast f)
        {
            Daily.Children.Clear();
            var days = f.Days.Take(5).ToList();
            if (days.Count == 0) return;
            double min = days.Min(d => d.Low), max = days.Max(d => d.High), span = Math.Max(1, max - min);
            var subtle = (Brush)FindResource("SubtleTextBrush");
            var accent = (Brush)FindResource("AccentBrush");

            foreach (var d in days)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

                string name = d.Date.Date == DateTime.Today ? "Today" : d.Date.ToString("ddd", CultureInfo.CurrentCulture);
                Add(row, 0, new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                Add(row, 1, new TextBlock { Text = WeatherService.Symbol(d.Code, true), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                Add(row, 2, new TextBlock { Text = d.RainChance >= 10 ? $"{d.RainChance}%" : "", FontSize = 10.5, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
                Add(row, 3, new TextBlock { Text = Deg(d.Low), FontSize = 12, Foreground = subtle, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

                // Range bar
                var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(2) });
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Background = new LinearGradientBrush(Color.FromRgb(0x7A, 0xB4, 0xFF), Color.FromRgb(0xFF, 0xB8, 0x6B), 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                track.Children.Add(fill);
                double lowFraction = (d.Low - min) / span, highFraction = (d.High - min) / span;
                track.SizeChanged += (_, _) =>
                {
                    fill.Margin = new Thickness(track.ActualWidth * lowFraction, 0, 0, 0);
                    fill.Width = Math.Max(4, track.ActualWidth * (highFraction - lowFraction));
                };
                Add(row, 4, track);

                Add(row, 5, new TextBlock { Text = Deg(d.High), FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                Daily.Children.Add(row);
            }
        }

        private static void Add(Grid grid, int column, UIElement element)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        // ---------------------------------------------------------------- options

        private void LoadOptionsIntoSwitches()
        {
            _loadingOptions = true;
            var o = _service.Options;
            FahrenheitToggle.IsChecked = o.Fahrenheit;
            KmhToggle.IsChecked = o.WindKmh;
            HourlyToggle.IsChecked = o.ShowHourly;
            DailyToggle.IsChecked = o.ShowDaily;
            _loadingOptions = false;
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingOptions) return;
            var o = _service.Options;
            bool unitsChanged = o.Fahrenheit != (FahrenheitToggle.IsChecked == true) || o.WindKmh != (KmhToggle.IsChecked == true);
            o.Fahrenheit = FahrenheitToggle.IsChecked == true;
            o.WindKmh = KmhToggle.IsChecked == true;
            o.ShowHourly = HourlyToggle.IsChecked == true;
            o.ShowDaily = DailyToggle.IsChecked == true;
            _service.SaveOptions();
            Show();
            if (unitsChanged) _ = Update();
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
            OptionsPopup.IsOpen = true;
            SearchBox.Focus();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = Update();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            _ = Search();
            e.Handled = true;
        }

        private void Search_Click(object sender, RoutedEventArgs e) => _ = Search();

        private async Task Search()
        {
            string query = SearchBox.Text.Trim();
            if (query.Length < 2) return;
            SearchResults.Children.Clear();
            SearchResults.Children.Add(new TextBlock { Text = "Searching…", FontSize = 11, Foreground = (Brush)FindResource("SubtleTextBrush") });
            try
            {
                var places = await WeatherService.SearchAsync(query);
                SearchResults.Children.Clear();
                if (places.Count == 0)
                    SearchResults.Children.Add(new TextBlock { Text = "No places found", FontSize = 11, Foreground = (Brush)FindResource("SubtleTextBrush") });
                foreach (var place in places)
                {
                    var button = new Button
                    {
                        Style = (Style)FindResource("PillButton"),
                        Content = new TextBlock { Text = place.ToString(), TextTrimming = TextTrimming.CharacterEllipsis },
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    button.Click += (_, _) => ChoosePlace(place);
                    SearchResults.Children.Add(button);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Place search failed: " + ex.Message);
                SearchResults.Children.Clear();
                SearchResults.Children.Add(new TextBlock { Text = "Search failed. Check your internet connection.", FontSize = 11, Foreground = (Brush)FindResource("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap });
            }
        }

        private void ChoosePlace(Place place)
        {
            var o = _service.Options;
            o.Place = place.Name;
            o.Latitude = place.Latitude;
            o.Longitude = place.Longitude;
            _service.SaveOptions();
            SearchResults.Children.Clear();
            SearchBox.Clear();
            OptionsPopup.IsOpen = false;
            _forecast = null;
            Show();
            _ = Update();
        }

        /// <summary>On the desktop the buttons only show while you're using the widget.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            bool show = !onDesktop || active;
            var d = TimeSpan.FromMilliseconds(220);
            foreach (var b in new UIElement[] { OptionsButton, RefreshButton })
            {
                b.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d));
                b.IsHitTestVisible = show;
            }
        }

        public void Stop() => _timer.Stop();
    }
}
