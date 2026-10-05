using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.SystemMonitor
{
    public partial class SystemView : UserControl
    {
        private const int HistoryLength = 60;

        private readonly SystemSampler _sampler;
        private readonly string _folder;
        private readonly SystemOptions _options;
        private readonly DispatcherTimer _timer;
        private readonly MetricCard _cpu, _memory, _disk, _gpu;
        private readonly UniformGrid _cores = new() { Margin = new Thickness(0, 2, 0, 6) };
        private readonly List<Border> _coreBars = new();
        private bool _active;
        private bool _sampling;
        private bool _loadingOptions;
        private DateTime _popupClosedAt;
        private List<(string name, ulong free, ulong total)> _drives = new();
        private DateTime _drivesRead = DateTime.MinValue;

        public SystemView(SystemSampler sampler, string folder)
        {
            _sampler = sampler;
            _folder = folder;
            _options = SystemOptions.Load(folder);
            InitializeComponent();

            _cpu = new MetricCard("CPU", "", Color.FromRgb(0x7A, 0xB4, 0xFF));
            _memory = new MetricCard("Memory", "", Color.FromRgb(0xB7, 0x9C, 0xFF));
            _disk = new MetricCard("Disk", "", Color.FromRgb(0x6F, 0xD8, 0xA6));
            _gpu = new MetricCard("GPU", "", Color.FromRgb(0xFF, 0xB8, 0x6B));
            _cpu.Detail.Children.Add(_cores);
            foreach (var card in new[] { _cpu, _memory, _disk, _gpu }) Cards.Children.Add(card.Root);

            _cpu.Sub.Text = _sampler.CpuName;
            _gpu.Sub.Text = _sampler.GpuName;

            // Updates every second while you're using the widget, every 2 seconds otherwise,
            // and not at all while it's hidden.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Refresh();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) { _timer.Start(); Refresh(); }
                else _timer.Stop();
            };

            OptionsPopup.Closed += (_, _) => _popupClosedAt = DateTime.UtcNow;
            LoadOptionsIntoSwitches();
            ApplyOptions();
        }

        // ---------------------------------------------------------------- readings

        private async void Refresh()
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                var options = _options.Clone();
                bool wantDrives = options.ShowDisk && options.DiskDetail && (DateTime.UtcNow - _drivesRead).TotalSeconds > 15;

                var (sample, drives) = await Task.Run(() =>
                {
                    var s = _sampler.Sample(options);
                    return (s, wantDrives ? ReadDrives() : null);
                });

                if (drives != null)
                {
                    _drives = drives;
                    _drivesRead = DateTime.UtcNow;
                }
                Show(sample);
            }
            catch (Exception ex)
            {
                Log.Write("System reading failed: " + ex.Message);
            }
            finally
            {
                _sampling = false;
            }
        }

        private void Show(SystemSample s)
        {
            if (_options.ShowCpu)
            {
                _cpu.SetValue(s.CpuPercent, _options.ShowGraphs);
                if (_options.CpuDetail)
                {
                    UpdateCores(s.CorePercents);
                    _cpu.SetRows(new[]
                    {
                        ("Processes", s.ProcessCount.ToString("N0")),
                        ("Threads", s.ThreadCount.ToString("N0")),
                        ("Logical processors", _sampler.ThreadsTotal.ToString()),
                        ("Up time", Uptime())
                    });
                }
            }

            if (_options.ShowMemory)
            {
                _memory.SetValue(s.MemoryPercent, _options.ShowGraphs);
                _memory.Sub.Text = $"{Bytes(s.MemoryTotal - s.MemoryAvailable)} of {Bytes(s.MemoryTotal)} in use";
                if (_options.MemoryDetail)
                    _memory.SetRows(new[]
                    {
                        ("Available", Bytes(s.MemoryAvailable)),
                        ("Committed", $"{Bytes(s.CommitUsed)} / {Bytes(s.CommitLimit)}"),
                        ("Cached", Bytes(s.Cached))
                    });
            }

            if (_options.ShowDisk)
            {
                _disk.SetValue(s.DiskPercent, _options.ShowGraphs);
                _disk.Sub.Text = $"Read {Rate(s.DiskReadBps)}  ·  Write {Rate(s.DiskWriteBps)}";
                if (_options.DiskDetail)
                {
                    var rows = s.Disks.Select(d => (d.Name, $"{d.ActivePercent:0}%  ·  R {Rate(d.ReadBps)}  W {Rate(d.WriteBps)}")).ToList();
                    rows.AddRange(_drives.Select(d => ($"{d.name} free", $"{Bytes(d.free)} of {Bytes(d.total)}")));
                    _disk.SetRows(rows);
                }
            }

            if (_options.ShowGpu)
            {
                if (!s.GpuAvailable)
                {
                    _gpu.Value.Text = "–";
                    _gpu.Sub.Text = "GPU usage isn't available on this PC";
                }
                else
                {
                    _gpu.SetValue(s.GpuPercent, _options.ShowGraphs);
                    _gpu.Sub.Text = _sampler.GpuName;
                    if (_options.GpuDetail)
                    {
                        var rows = new List<(string, string)>
                        {
                            ("Dedicated memory", _sampler.GpuDedicatedTotal > 0
                                ? $"{Bytes(s.GpuDedicatedUsed)} / {Bytes(_sampler.GpuDedicatedTotal)}"
                                : Bytes(s.GpuDedicatedUsed)),
                            ("Shared memory", Bytes(s.GpuSharedUsed))
                        };
                        rows.AddRange(s.GpuEngines.Select(e => (e.engine, $"{e.percent:0}%")));
                        _gpu.SetRows(rows);
                    }
                }
            }
        }

        private void UpdateCores(double[] cores)
        {
            if (_coreBars.Count != cores.Length)
            {
                _cores.Children.Clear();
                _coreBars.Clear();
                _cores.Columns = cores.Length <= 8 ? cores.Length : (int)Math.Ceiling(cores.Length / 2.0);
                foreach (var _ in cores)
                {
                    var fill = new Border
                    {
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Background = _cpu.Accent,
                        CornerRadius = new CornerRadius(1.5),
                        Height = 0
                    };
                    var track = new Border
                    {
                        Height = 18,
                        Margin = new Thickness(1.5, 1.5, 1.5, 1.5),
                        CornerRadius = new CornerRadius(1.5),
                        Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                        Child = fill
                    };
                    _coreBars.Add(fill);
                    _cores.Children.Add(track);
                }
            }
            for (int i = 0; i < cores.Length; i++) _coreBars[i].Height = 18 * cores[i] / 100.0;
        }

        private static List<(string, ulong, ulong)> ReadDrives()
        {
            var list = new List<(string, ulong, ulong)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    list.Add((d.Name.TrimEnd('\\'), (ulong)d.AvailableFreeSpace, (ulong)d.TotalSize));
                }
                catch
                {
                    // drive went away
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- options

        private void LoadOptionsIntoSwitches()
        {
            _loadingOptions = true;
            ShowCpu.IsChecked = _options.ShowCpu;
            CpuDetail.IsChecked = _options.CpuDetail;
            ShowMemory.IsChecked = _options.ShowMemory;
            MemoryDetail.IsChecked = _options.MemoryDetail;
            ShowDisk.IsChecked = _options.ShowDisk;
            DiskDetail.IsChecked = _options.DiskDetail;
            ShowGpu.IsChecked = _options.ShowGpu;
            GpuDetail.IsChecked = _options.GpuDetail;
            ShowGraphs.IsChecked = _options.ShowGraphs;
            _loadingOptions = false;
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingOptions) return;
            _options.ShowCpu = ShowCpu.IsChecked == true;
            _options.CpuDetail = CpuDetail.IsChecked == true;
            _options.ShowMemory = ShowMemory.IsChecked == true;
            _options.MemoryDetail = MemoryDetail.IsChecked == true;
            _options.ShowDisk = ShowDisk.IsChecked == true;
            _options.DiskDetail = DiskDetail.IsChecked == true;
            _options.ShowGpu = ShowGpu.IsChecked == true;
            _options.GpuDetail = GpuDetail.IsChecked == true;
            _options.ShowGraphs = ShowGraphs.IsChecked == true;
            _options.Save(_folder);
            if (_options.DiskDetail) _drivesRead = DateTime.MinValue; // read drive space straight away
            ApplyOptions();
            Refresh();
        }

        private void ApplyOptions()
        {
            _cpu.SetVisible(_options.ShowCpu, _options.CpuDetail, _options.ShowGraphs);
            _memory.SetVisible(_options.ShowMemory, _options.MemoryDetail, _options.ShowGraphs);
            _disk.SetVisible(_options.ShowDisk, _options.DiskDetail, _options.ShowGraphs);
            _gpu.SetVisible(_options.ShowGpu, _options.GpuDetail, _options.ShowGraphs);

            CpuDetail.IsEnabled = _options.ShowCpu;
            MemoryDetail.IsEnabled = _options.ShowMemory;
            DiskDetail.IsEnabled = _options.ShowDisk;
            GpuDetail.IsEnabled = _options.ShowGpu;

            bool any = _options.ShowCpu || _options.ShowMemory || _options.ShowDisk || _options.ShowGpu;
            NothingShown.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            // A click on the button also closes the popup; don't immediately reopen it
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
            OptionsPopup.IsOpen = true;
        }

        /// <summary>On the desktop the options button only shows while you're using the widget.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            _active = active;
            bool show = !onDesktop || active || OptionsPopup.IsOpen;
            OptionsButton.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(220)));
            OptionsButton.IsHitTestVisible = show;

            _timer.Interval = TimeSpan.FromSeconds(_active ? 1 : 2);
        }

        public void Stop() => _timer.Stop();

        // ---------------------------------------------------------------- formatting

        private static string Bytes(ulong bytes)
        {
            double b = bytes;
            if (b >= 1024d * 1024 * 1024 * 1024) return $"{b / (1024d * 1024 * 1024 * 1024):0.0} TB";
            if (b >= 1024d * 1024 * 1024) return $"{b / (1024d * 1024 * 1024):0.0} GB";
            if (b >= 1024d * 1024) return $"{b / (1024d * 1024):0} MB";
            return $"{b / 1024d:0} KB";
        }

        private static string Rate(double bps)
        {
            if (bps >= 1024d * 1024 * 1024) return $"{bps / (1024d * 1024 * 1024):0.0} GB/s";
            if (bps >= 1024d * 1024) return $"{bps / (1024d * 1024):0.0} MB/s";
            if (bps >= 1024) return $"{bps / 1024d:0} KB/s";
            return "0 KB/s";
        }

        private static string Uptime()
        {
            var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{t.Minutes}m";
        }
    }

    /// <summary>One reading on screen: icon, name, big number, a summary line, history graph and detail rows.</summary>
    internal sealed class MetricCard
    {
        private const double GraphHeight = 30;
        private readonly Queue<double> _history = new();
        private readonly Canvas _graph;
        private readonly Polyline _line;
        private readonly Polygon _area;
        private readonly Border _barTrack;
        private readonly Border _barFill;
        private readonly Grid _rows = new();
        private readonly List<(TextBlock key, TextBlock value)> _rowCells = new();

        public MetricCard(string name, string glyph, Color color)
        {
            Accent = new SolidColorBrush(color);
            Accent.Freeze();
            var soft = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B));
            soft.Freeze();
            var subtle = (Brush)Application.Current.FindResource("SubtleTextBrush");

            Root = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            // Header: icon · name + summary · big value
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 16,
                Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            Sub = new TextBlock { FontSize = 11, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis };
            names.Children.Add(Sub);
            Grid.SetColumn(names, 1);
            header.Children.Add(names);

            Value = new TextBlock { FontSize = 22, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Text = "–" };
            Grid.SetColumn(Value, 2);
            header.Children.Add(Value);
            Root.Children.Add(header);

            // History graph
            _line = new Polyline { Stroke = Accent, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
            _area = new Polygon { Fill = soft };
            _graph = new Canvas { Height = GraphHeight, Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
            _graph.Children.Add(_area);
            _graph.Children.Add(_line);
            _graph.SizeChanged += (_, _) => Redraw();
            Root.Children.Add(_graph);

            // Simple bar, used when graphs are off
            _barFill = new Border { Background = Accent, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _barTrack = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Child = _barFill
            };
            _barTrack.SizeChanged += (_, _) => UpdateBar();
            Root.Children.Add(_barTrack);

            // Detail rows
            _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Detail = new StackPanel { Margin = new Thickness(28, 8, 0, 0) };
            Detail.Children.Add(_rows);
            Root.Children.Add(Detail);
        }

        public StackPanel Root { get; }
        public StackPanel Detail { get; }
        public TextBlock Value { get; }
        public TextBlock Sub { get; }
        public SolidColorBrush Accent { get; }

        private double _last;

        public void SetVisible(bool shown, bool detail, bool graph)
        {
            Root.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            Detail.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
            _graph.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
            _barTrack.Visibility = graph ? Visibility.Collapsed : Visibility.Visible;
            if (!shown) _history.Clear(); // a hidden reading keeps no history
        }

        public void SetValue(double percent, bool graph)
        {
            _last = Math.Clamp(percent, 0, 100);
            Value.Text = $"{_last:0}%";
            _history.Enqueue(_last);
            while (_history.Count > 60) _history.Dequeue();
            if (graph) Redraw(); else UpdateBar();
        }

        private void UpdateBar() => _barFill.Width = _barTrack.ActualWidth * _last / 100.0;

        private void Redraw()
        {
            double w = _graph.ActualWidth, h = GraphHeight;
            if (w <= 0 || _history.Count == 0) return;

            // Newest on the right; the graph fills from the right as history builds up
            double step = w / (60 - 1);
            double x = w - step * (_history.Count - 1);
            var line = new PointCollection(_history.Count);
            foreach (double v in _history)
            {
                line.Add(new Point(x, h - 1 - (h - 2) * v / 100.0));
                x += step;
            }
            var area = new PointCollection(line) { new Point(w, h), new Point(line[0].X, h) };
            line.Freeze();
            area.Freeze();
            _line.Points = line;
            _area.Points = area;
        }

        /// <summary>Shows label/value rows, reusing the text blocks from last time.</summary>
        public void SetRows(IReadOnlyList<(string key, string value)> rows)
        {
            var subtle = (Brush)Application.Current.FindResource("SubtleTextBrush");
            while (_rowCells.Count < rows.Count)
            {
                int r = _rowCells.Count;
                _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { FontSize = 11, Foreground = subtle, Margin = new Thickness(0, 1, 14, 1) };
                var value = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(key, r);
                Grid.SetRow(value, r);
                Grid.SetColumn(value, 1);
                _rows.Children.Add(key);
                _rows.Children.Add(value);
                _rowCells.Add((key, value));
            }
            for (int i = 0; i < _rowCells.Count; i++)
            {
                bool used = i < rows.Count;
                var (key, value) = _rowCells[i];
                key.Visibility = value.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
                if (!used) continue;
                if (key.Text != rows[i].key) key.Text = rows[i].key;
                if (value.Text != rows[i].value) value.Text = rows[i].value;
            }
        }
    }
}
