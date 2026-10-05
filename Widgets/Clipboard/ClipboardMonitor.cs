using System;
using System.Windows.Interop;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Widgets.Clipboard
{
    /// <summary>
    /// Asks Windows to tell us whenever the clipboard changes (the official listener API,
    /// the same mechanism Win+V uses), via a hidden message-only window.
    /// </summary>
    public sealed class ClipboardMonitor : IDisposable
    {
        private readonly HwndSource _source;

        public event EventHandler? Changed;

        public ClipboardMonitor()
        {
            var p = new HwndSourceParameters("GlasspaneClipboardListener")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);

            if (!AddClipboardFormatListener(_source.Handle))
                Core.Log.Write("Could not start listening to the clipboard");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                handled = true;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.Dispose();
        }
    }
}
