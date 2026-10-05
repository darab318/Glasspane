using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Clipboard
{
    /// <summary>
    /// Extended clipboard history: unlimited, searchable, survives restarts, handles
    /// text (with formatting), images and files, and skips anything password managers
    /// mark as private.
    /// </summary>
    public sealed class ClipboardWidget : IWidget
    {
        private readonly WidgetContext _context;
        private readonly int _historyLimit;
        private readonly ClipboardStore _store;
        private readonly ClipboardMonitor _monitor;
        private ClipboardView? _view;

        public ClipboardWidget(WidgetContext context, int historyLimit)
        {
            _context = context;
            _historyLimit = historyLimit;
            _store = new ClipboardStore(context.FolderFor(this));
            _monitor = new ClipboardMonitor();
        }

        public string Id => "clipboard";
        public string Title => "Clipboard";
        public string Glyph => "\uE77F";
        public string Description => "Everything you copy, searchable, with images and files";
        public Size DefaultSize => new(380, 600);

        public FrameworkElement CreateView()
        {
            _view ??= new ClipboardView(_store, _monitor, _context.Foreground, _historyLimit);
            return _view;
        }

        public void OnSummoned() => _view?.FocusSearch();

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose()
        {
            _monitor.Dispose();
            _store.Dispose();
        }
    }
}
