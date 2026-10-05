using System.Windows;
using Glasspane.Core;

namespace Glasspane.Widgets.Audio
{
    /// <summary>
    /// Volume, mute and a live level meter for the current output, one-click switching between
    /// outputs (speakers, headphones, monitor…), and per-app volumes.
    /// </summary>
    public sealed class AudioWidget : IWidget
    {
        private readonly AudioService _audio;
        private AudioView? _view;

        public AudioWidget()
        {
            _audio = new AudioService();
        }

        public string Id => "audio";
        public string Title => "Audio";
        public string Glyph => "";
        public string Description => "Volume, switch speakers and headphones, app volumes";
        public bool FillsHeight => false;
        public Size DefaultSize => new(360, 220);

        public FrameworkElement CreateView()
        {
            _view ??= new AudioView(_audio);
            return _view;
        }

        public void OnPresentationChanged(bool onDesktop, bool active) => _view?.SetPresentation(onDesktop, active);

        public void Dispose()
        {
            _view?.Detach();
            _audio.Dispose();
        }
    }
}
