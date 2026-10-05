using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Shelf
{
    /// <summary>Your newest screenshots and downloads: click to copy, drag into any app.</summary>
    public sealed class ShelfWidget : IWidget
    {
        private readonly ShelfService _service = new();
        private ShelfView? _view;

        public string Id => "shelf";
        public string Title => "Screenshots & Downloads";
        public string Glyph => "";
        public string Description => "Newest screenshots and downloads; click to copy, drag into any app";
        public Size DefaultSize => new(340, 420);

        public FrameworkElement CreateView()
        {
            _view ??= new ShelfView(_service);
            return _view;
        }

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose()
        {
            _view?.Stop();
            _service.Dispose();
        }
    }
}
