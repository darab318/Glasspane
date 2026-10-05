using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glasspane.Core;

namespace Glasspane.Widgets.Notes
{
    public sealed class Note
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = "";
    }

    /// <summary>Sticky notes on the desktop. Several notes, switched with the chips; saves as you type.</summary>
    public partial class NotesView : UserControl
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private readonly string _file;
        private readonly DispatcherTimer _saveDebounce;
        private readonly List<Note> _notes;
        private int _current;
        private bool _loading;

        public NotesView(string folder)
        {
            InitializeComponent();
            Directory.CreateDirectory(folder);
            _file = Path.Combine(folder, "notes.json");
            _notes = Load();
            if (_notes.Count == 0) _notes.Add(new Note());

            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Save(); };

            ShowNote(0);
        }

        private List<Note> Load()
        {
            try
            {
                if (File.Exists(_file))
                    return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(_file)) ?? new List<Note>();
            }
            catch (Exception ex)
            {
                Log.Write("Notes could not be read: " + ex.Message);
                // Keep the unreadable file rather than overwriting it
                try { File.Copy(_file, _file + ".broken", overwrite: true); } catch { }
            }
            return new List<Note>();
        }

        private void Save()
        {
            try
            {
                string tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_notes, Json));
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Notes could not be saved: " + ex.Message);
            }
        }

        /// <summary>Saves straight away (used when the app closes).</summary>
        public void Flush()
        {
            if (_saveDebounce.IsEnabled)
            {
                _saveDebounce.Stop();
                Save();
            }
        }

        // ---------------------------------------------------------------- notes

        private void ShowNote(int index)
        {
            _current = Math.Clamp(index, 0, _notes.Count - 1);
            _loading = true;
            Editor.Text = _notes[_current].Text;
            Editor.CaretIndex = Editor.Text.Length;
            _loading = false;
            Placeholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildChips();
        }

        private void BuildChips()
        {
            Chips.Children.Clear();
            for (int i = 0; i < _notes.Count; i++)
            {
                int index = i;
                var chip = new RadioButton
                {
                    Style = (Style)FindResource("Chip"),
                    Content = ChipTitle(_notes[i], i),
                    IsChecked = i == _current,
                    GroupName = "Notes" + GetHashCode()
                };
                chip.Checked += (_, _) => { if (index != _current) ShowNote(index); };
                Chips.Children.Add(chip);
            }
            DeleteButton.IsEnabled = _notes.Count > 1 || _notes[0].Text.Length > 0;
            // Only one note: no need for chips
            ChipScroller.Visibility = _notes.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>A note's chip shows its first line, shortened.</summary>
        private static string ChipTitle(Note note, int index)
        {
            string first = note.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            if (first.Length == 0) return $"Note {index + 1}";
            return first.Length > 16 ? first.Substring(0, 15) + "…" : first;
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            string oldTitle = ChipTitle(_notes[_current], _current);
            _notes[_current].Text = Editor.Text;
            Placeholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (ChipTitle(_notes[_current], _current) != oldTitle && Chips.Children.Count > _current &&
                Chips.Children[_current] is RadioButton chip)
                chip.Content = ChipTitle(_notes[_current], _current);
            DeleteButton.IsEnabled = _notes.Count > 1 || Editor.Text.Length > 0;

            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            _notes.Add(new Note());
            Save();
            ShowNote(_notes.Count - 1);
            Editor.Focus();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            string text = _notes[_current].Text.Trim();
            if (text.Length > 0)
            {
                string preview = text.Length > 60 ? text.Substring(0, 60) + "…" : text;
                if (MessageBox.Show($"Delete this note?\n\n\"{preview}\"", "Glasspane", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }

            _notes.RemoveAt(_current);
            if (_notes.Count == 0) _notes.Add(new Note());
            Save();
            ShowNote(Math.Min(_current, _notes.Count - 1));
        }

        private void Chips_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ChipScroller.ScrollToHorizontalOffset(ChipScroller.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        /// <summary>On the desktop the chips and buttons only show while you're using the widget.</summary>
        public void SetPresentation(bool onDesktop, bool active)
        {
            bool show = !onDesktop || active;
            TopRow.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(220)));
            TopRow.IsHitTestVisible = show;
        }
    }
}
