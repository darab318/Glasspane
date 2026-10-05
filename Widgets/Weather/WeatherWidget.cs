using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Weather
{
    /// <summary>Current weather, next hours and next 5 days (Open-Meteo; no account needed).</summary>
    public sealed class WeatherWidget : IWidget
    {
        private readonly WeatherService _service;
        private WeatherView? _view;

        public WeatherWidget(WidgetContext context) => _service = new WeatherService(context.FolderFor(this));

        public string Id => "weather";
        public string Title => "Weather";
        public string Glyph => "";
        public string Description => "Now, the next hours and the next 5 days; updates every 30 minutes";
        public bool FillsHeight => false;
        public Size DefaultSize => new(330, 330);

        public FrameworkElement CreateView()
        {
            _view ??= new WeatherView(_service);
            return _view;
        }

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose() => _view?.Stop();
    }
}
