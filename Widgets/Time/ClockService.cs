using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Time
{
    public sealed class Alarm : INotifyPropertyChanged
    {
        private bool _enabled = true;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Hour { get; set; }
        public int Minute { get; set; }
        public string Label { get; set; } = "";

        /// <summary>Repeat days as bits: Monday = 1, Tuesday = 2 … Sunday = 64. 0 = once.</summary>
        public int Days { get; set; }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; Changed(nameof(Enabled)); }
        }

        [JsonIgnore] public string TimeText => ClockFormat.Time(Hour, Minute);

        [JsonIgnore] public string LabelText => string.IsNullOrWhiteSpace(Label) ? "Alarm" : Label;

        [JsonIgnore]
        public string RepeatText => Days switch
        {
            0 => "Once",
            127 => "Every day",
            31 => "Weekdays",
            96 => "Weekends",
            _ => string.Join(", ", Enumerable.Range(0, 7).Where(i => (Days & (1 << i)) != 0).Select(i => ShortDay[i]))
        };

        private static readonly string[] ShortDay = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

        public bool OccursOn(DayOfWeek day) => Days == 0 || (Days & DayBit(day)) != 0;

        /// <summary>The next time this alarm goes off after <paramref name="from"/>.</summary>
        public DateTime? NextAfter(DateTime from)
        {
            if (!Enabled) return null;
            for (int d = 0; d <= 7; d++)
            {
                var candidate = from.Date.AddDays(d).AddHours(Hour).AddMinutes(Minute);
                if (candidate > from && OccursOn(candidate.DayOfWeek)) return candidate;
            }
            return null;
        }

        public void RefreshText() => Changed(nameof(TimeText));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ClockData
    {
        public List<Alarm> Alarms { get; set; } = new();
        public bool ShowSeconds { get; set; }
        public bool Use24Hour { get; set; } = true;
    }

    internal static class ClockFormat
    {
        public static bool Use24Hour { get; set; } = true;

        public static string Time(int hour, int minute) =>
            Use24Hour ? $"{hour:00}:{minute:00}" : $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minute:00} {(hour < 12 ? "am" : "pm")}";

        public static string Duration(TimeSpan t, bool tenths = false)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            string main = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
            return tenths ? $"{main}.{t.Milliseconds / 100}" : main;
        }
    }

    /// <summary>
    /// Alarms, the countdown timer and the stopwatch. These keep running when the clock widget is
    /// hidden. Instead of checking constantly, a single timer wakes up when the next alarm or timer
    /// is due (and at least every 30 seconds, so alarms still go off correctly after sleep or a
    /// clock change).
    /// </summary>
    public sealed class ClockService : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly string _file;
        private readonly DispatcherTimer _wake = new();
        private readonly List<(Alarm alarm, DateTime due)> _snoozed = new();
        private DateTime _lastCheck = DateTime.Now;

        public ClockService(string folder)
        {
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "clock.json");
            Data = Load();
            ClockFormat.Use24Hour = Data.Use24Hour;
            _wake.Tick += (_, _) => Check();
            Reschedule();
        }

        public ClockData Data { get; }

        /// <summary>Alarms were added, removed or switched on/off.</summary>
        public event Action? AlarmsChanged;

        /// <summary>The timer started, paused, finished or was reset.</summary>
        public event Action? TimerChanged;

        // ---------------------------------------------------------------- storage

        private ClockData Load()
        {
            try
            {
                if (File.Exists(_file))
                    return JsonSerializer.Deserialize<ClockData>(File.ReadAllText(_file)) ?? new ClockData();
            }
            catch (Exception ex)
            {
                Log.Write("Clock settings could not be read: " + ex.Message);
            }
            return new ClockData();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(Data, Json));
            }
            catch (Exception ex)
            {
                Log.Write("Clock settings could not be saved: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- alarms

        public void AddAlarm(Alarm alarm)
        {
            Data.Alarms.Add(alarm);
            Data.Alarms.Sort((a, b) => (a.Hour * 60 + a.Minute).CompareTo(b.Hour * 60 + b.Minute));
            AlarmsUpdated();
        }

        public void RemoveAlarm(Alarm alarm)
        {
            Data.Alarms.Remove(alarm);
            _snoozed.RemoveAll(s => s.alarm == alarm);
            AlarmsUpdated();
        }

        /// <summary>Call after changing an alarm (e.g. switching it on or off).</summary>
        public void AlarmsUpdated()
        {
            Save();
            Reschedule();
            AlarmsChanged?.Invoke();
        }

        public DateTime? NextAlarm()
        {
            var now = DateTime.Now;
            var times = Data.Alarms.Select(a => a.NextAfter(now)).Where(t => t != null).Select(t => t!.Value)
                .Concat(_snoozed.Select(s => s.due));
            return times.Any() ? times.Min() : null;
        }

        // ---------------------------------------------------------------- timer

        private DateTime? _timerEnd;
        private TimeSpan _timerPaused;

        public TimeSpan TimerTotal { get; private set; }
        public bool TimerRunning => _timerEnd != null;
        public bool TimerPaused => _timerEnd == null && _timerPaused > TimeSpan.Zero;
        public DateTime? TimerEndsAt => _timerEnd;

        public TimeSpan TimerRemaining
        {
            get
            {
                if (_timerEnd is DateTime end)
                {
                    var left = end - DateTime.Now;
                    return left > TimeSpan.Zero ? left : TimeSpan.Zero;
                }
                return _timerPaused;
            }
        }

        public void StartTimer(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) return;
            TimerTotal = duration;
            _timerPaused = TimeSpan.Zero;
            _timerEnd = DateTime.Now + duration;
            TimerUpdated();
        }

        public void PauseTimer()
        {
            if (_timerEnd == null) return;
            _timerPaused = TimerRemaining;
            _timerEnd = null;
            TimerUpdated();
        }

        public void ResumeTimer()
        {
            if (_timerEnd != null || _timerPaused <= TimeSpan.Zero) return;
            _timerEnd = DateTime.Now + _timerPaused;
            _timerPaused = TimeSpan.Zero;
            TimerUpdated();
        }

        public void ResetTimer()
        {
            _timerEnd = null;
            _timerPaused = TimeSpan.Zero;
            TimerTotal = TimeSpan.Zero;
            TimerUpdated();
        }

        private void TimerUpdated()
        {
            Reschedule();
            TimerChanged?.Invoke();
        }

        // ---------------------------------------------------------------- stopwatch

        public Stopwatch Stopwatch { get; } = new();
        public List<TimeSpan> Laps { get; } = new();

        // ---------------------------------------------------------------- scheduling

        private void Reschedule()
        {
            var now = DateTime.Now;
            var next = NextAlarm();
            if (_timerEnd is DateTime end && (next == null || end < next)) next = end;

            var wait = next is DateTime due ? due - now : TimeSpan.FromSeconds(30);
            if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            if (wait < TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);

            _wake.Stop();
            _wake.Interval = wait;
            _wake.Start();
        }

        private void Check()
        {
            var now = DateTime.Now;
            bool changed = false;

            // Alarms whose time fell between the last check and now (handles sleep and clock changes;
            // a gap of more than a day only fires each alarm once)
            if (now < _lastCheck) _lastCheck = now; // clock went backwards
            foreach (var alarm in Data.Alarms.ToList())
            {
                if (!alarm.Enabled) continue;
                var occurrence = alarm.NextAfter(_lastCheck);
                if (occurrence == null || occurrence > now) continue;

                Alert.Raise("Alarm", alarm.LabelText, alarm.TimeText, canSnooze: true,
                    snooze: () => { _snoozed.Add((alarm, DateTime.Now.AddMinutes(5))); Reschedule(); AlarmsChanged?.Invoke(); });

                if (alarm.Days == 0)
                {
                    alarm.Enabled = false; // one-off alarms switch themselves off
                    changed = true;
                }
            }

            foreach (var snooze in _snoozed.Where(s => s.due <= now).ToList())
            {
                _snoozed.Remove(snooze);
                var alarm = snooze.alarm;
                Alert.Raise("Alarm (snoozed)", alarm.LabelText, alarm.TimeText, canSnooze: true,
                    snooze: () => { _snoozed.Add((alarm, DateTime.Now.AddMinutes(5))); Reschedule(); AlarmsChanged?.Invoke(); });
            }

            if (_timerEnd is DateTime end && end <= now)
            {
                var total = TimerTotal;
                _timerEnd = null;
                _timerPaused = TimeSpan.Zero;
                Alert.Raise("Timer done", ClockFormat.Duration(total) + " timer", "Finished at " + ClockFormat.Time(now.Hour, now.Minute),
                    canSnooze: false, snooze: null);
                TimerChanged?.Invoke();
            }

            _lastCheck = now;
            if (changed) AlarmsUpdated();
            else Reschedule();
        }

        public void Dispose()
        {
            _wake.Stop();
            Save();
        }
    }
}
