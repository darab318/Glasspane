using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.SystemMonitor
{
    /// <summary>
    /// CPU, memory, disk and GPU usage with history graphs. Each reading can be shown or hidden,
    /// and switched between a simple and a detailed view, from the widget's options button.
    /// </summary>
    public sealed class SystemWidget : IWidget
    {
        private readonly string _folder;
        private readonly SystemSampler _sampler = new();
        private SystemView? _view;

        public SystemWidget(WidgetContext context)
        {
            _folder = context.FolderFor(this);
        }

        public string Id => "system";
        public string Title => "System";
        public string Glyph => "";
        public string Description => "CPU, memory, disk and GPU usage, simple or detailed";
        public bool FillsHeight => false;
        public Size DefaultSize => new(340, 420);

        public FrameworkElement CreateView()
        {
            _view ??= new SystemView(_sampler, _folder);
            return _view;
        }

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose()
        {
            _view?.Stop();
            _sampler.Dispose();
        }
    }
}
