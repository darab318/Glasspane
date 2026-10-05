using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Notes
{
    /// <summary>Sticky notes that live on the desktop and save as you type.</summary>
    public sealed class NotesWidget : IWidget
    {
        private readonly string _folder;
        private NotesView? _view;

        public NotesWidget(WidgetContext context) => _folder = context.FolderFor(this);

        public string Id => "notes";
        public string Title => "Notes";
        public string Glyph => "";
        public string Description => "Sticky notes on your desktop that save as you type";
        public Size DefaultSize => new(320, 300);

        public FrameworkElement CreateView()
        {
            _view ??= new NotesView(_folder);
            return _view;
        }

        public void OnSummoned() => _view?.Editor.Focus();

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose() => _view?.Flush();
    }
}
