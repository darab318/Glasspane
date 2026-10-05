using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Glasspane.Core;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Shell
{
    /// <summary>
    /// Owns every widget and every glass window, and moves widgets between windows:
    /// split one off into its own window, or join two windows by dragging one onto the other.
    /// </summary>
    public sealed class WidgetManager
    {
        private readonly SettingsStore _store;
        private readonly Dictionary<string, IWidget> _widgets = new();
        private readonly List<WidgetWindow> _windows = new();
        private WidgetWindow? _dropTarget;

        public WidgetManager(SettingsStore store)
        {
            _store = store;
        }

        public IReadOnlyList<WidgetWindow> Windows => _windows;
        public IEnumerable<IWidget> AllWidgets => _widgets.Values;

        /// <summary>Raised when windows are created, closed, joined or split.</summary>
        public event EventHandler? LayoutChanged;

        public void Save() => _store.Save();

        public void Register(IWidget widget) => _widgets[widget.Id] = widget;

        /// <summary>
        /// Recreates the saved windows. Widgets that aren't in any window yet (e.g. one added in an
        /// update) join the first window at the top, so new features show up together.
        /// </summary>
        public void Start()
        {
            var settings = _store.Current;
            var placed = new HashSet<string>();

            foreach (var ws in settings.Windows.ToList())
            {
                var widgets = ws.WidgetIds
                    .Where(id => _widgets.ContainsKey(id) && placed.Add(id))
                    .Select(id => _widgets[id])
                    .ToList();
                ws.WidgetIds = widgets.Select(w => w.Id).ToList();

                if (widgets.Count == 0)
                {
                    settings.Windows.Remove(ws);
                    continue;
                }
                if (string.IsNullOrEmpty(ws.Id)) ws.Id = NewWindowId(widgets[0]);
                Create(ws, widgets);
            }

            // A widget added in an update gets its own window, so it doesn't squeeze into an
            // existing one. Drag it onto another window to combine them.
            foreach (var widget in _widgets.Values.Where(w => !placed.Contains(w.Id)))
            {
                var ws = new WidgetSettings { Id = NewWindowId(widget), WidgetIds = { widget.Id } };
                settings.Windows.Add(ws);
                Create(ws, new[] { widget });
            }

            Save();
        }

        private WidgetWindow Create(WidgetSettings ws, IEnumerable<IWidget> widgets)
        {
            var window = new WidgetWindow(ws, widgets, this);
            window.ModeChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
            window.IsVisibleChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
            _windows.Add(window);
            if (ws.Visible) window.Show();
            return window;
        }

        private static string NewWindowId(IWidget first) => $"{first.Id}-{Guid.NewGuid():N}".Substring(0, first.Id.Length + 9);

        public WidgetWindow? WindowOf(string widgetId) =>
            _windows.FirstOrDefault(w => w.Widgets.Any(x => x.Id == widgetId));

        /// <summary>Shows the window holding a widget and gives that widget focus.</summary>
        public void Summon(string widgetId)
        {
            var window = WindowOf(widgetId);
            if (window == null || !_widgets.TryGetValue(widgetId, out var widget)) return;
            window.Summon(widget);
        }

        // ---------------------------------------------------------------- on / off

        /// <summary>True if the widget is currently showing (on the desktop or as a window).</summary>
        public bool IsShown(string widgetId) => WindowOf(widgetId) is { } w && w.IsVisible;

        /// <summary>
        /// Turns a widget on or off. Turning off a widget that shares a window moves it out
        /// (hidden) so the others stay; turning it back on brings it back where it was last.
        /// </summary>
        public void SetShown(string widgetId, bool show)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget)) return;
            var window = WindowOf(widgetId);

            if (show)
            {
                if (window != null)
                {
                    window.Summon(widget);
                }
                else
                {
                    var ws = new WidgetSettings { Id = NewWindowId(widget), WidgetIds = { widget.Id } };
                    _store.Current.Windows.Add(ws);
                    Create(ws, new[] { widget }).Summon(widget);
                }
            }
            else if (window != null)
            {
                if (window.Widgets.Count > 1) Split(window, widget, show: false);
                else window.HideWidget();
            }

            Save();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- split

        /// <summary>Moves a widget out of a shared window into a new window of its own, alongside it.</summary>
        /// <param name="show">False to split it off hidden (used when turning a widget off).</param>
        public void Split(WidgetWindow from, IWidget widget, bool show = true)
        {
            if (from.Widgets.Count < 2) return;
            from.RemoveWidget(widget);

            var src = from.Settings;
            double width = from.ActualWidth;
            double left = from.Left + width + 12;
            if (left + width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
                left = from.Left - width - 12; // no room on the right: open on the left instead

            var ws = new WidgetSettings
            {
                Id = NewWindowId(widget),
                WidgetIds = { widget.Id },
                Left = left,
                Top = from.Top,
                Width = width,
                Mode = src.Mode,
                BackgroundOpacity = src.BackgroundOpacity,
                BlurStrength = src.BlurStrength,
                DesktopOpacity = src.DesktopOpacity,
                DesktopBlurStrength = src.DesktopBlurStrength,
                Visible = show
            };
            _store.Current.Windows.Add(ws);
            var window = Create(ws, new[] { widget });
            if (show) window.Summon(widget);

            Save();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- join by dragging

        /// <summary>Called while a window is being dragged: highlights the window under the cursor.</summary>
        public void UpdateDrag(WidgetWindow dragged)
        {
            GetCursorPos(out POINT cursor);
            WidgetWindow? target = null;
            foreach (var w in _windows)
            {
                if (w == dragged || !w.IsVisible || w.Handle == IntPtr.Zero) continue;
                if (GetWindowRect(w.Handle, out RECT r) && r.Contains(cursor))
                {
                    target = w;
                    break;
                }
            }

            if (target == _dropTarget) return;
            _dropTarget?.ShowDropHint(false);
            _dropTarget = target;
            _dropTarget?.ShowDropHint(true);
        }

        /// <summary>Called when the drag ends: if it was released over another window, the two join.</summary>
        public void EndDrag(WidgetWindow dragged)
        {
            var target = _dropTarget;
            _dropTarget?.ShowDropHint(false);
            _dropTarget = null;
            if (target == null || target == dragged) return;

            foreach (var widget in dragged.Widgets.ToList())
            {
                dragged.RemoveWidget(widget);
                target.AddWidget(widget);
            }

            _windows.Remove(dragged);
            _store.Current.Windows.Remove(dragged.Settings);
            dragged.CloseForExit();

            target.Summon();
            Save();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- shutdown

        public void CloseAll()
        {
            foreach (var w in _windows.ToList()) w.CloseForExit();
            foreach (var widget in _widgets.Values) widget.Dispose();
            Save();
        }
    }
}
