using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Time
{
    /// <summary>Clock and date, with alarms, a countdown timer and a stopwatch.</summary>
    public sealed class ClockWidget : IWidget
    {
        // Alarms and the timer live outside the view so they go off even while the widget is hidden
        private readonly ClockService _clock;
        private ClockView? _view;

        public ClockWidget(WidgetContext context)
        {
            _clock = new ClockService(context.FolderFor(this));
        }

        public string Id => "clock";
        public string Title => "Clock";
        public string Glyph => "";
        public string Description => "Time and date, with alarms, a timer and a stopwatch";
        public bool FillsHeight => false;
        public Size DefaultSize => new(320, 200);

        public FrameworkElement CreateView()
        {
            _view ??= new ClockView(_clock);
            return _view;
        }

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose()
        {
            _view?.Stop();
            _clock.Dispose();
        }
    }
}
