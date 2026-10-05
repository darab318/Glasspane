using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.NowPlaying
{
    /// <summary>The song or video that's playing, with album art, play/pause/skip and progress.</summary>
    public sealed class NowPlayingWidget : IWidget
    {
        private NowPlayingView? _view;

        public string Id => "nowplaying";
        public string Title => "Now Playing";
        public string Glyph => "";
        public string Description => "What's playing in Spotify, YouTube and more, with controls";
        public bool FillsHeight => false;
        public Size DefaultSize => new(360, 140);

        public FrameworkElement CreateView()
        {
            _view ??= new NowPlayingView();
            return _view;
        }

        public void Dispose() => _view?.Detach();
    }
}
