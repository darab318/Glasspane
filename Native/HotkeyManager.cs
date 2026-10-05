using System;
using System.Collections.Generic;
using System.Windows.Interop;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Native
{
    /// <summary>System-wide keyboard shortcuts, delivered on a hidden message window.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private int _nextId = 0xB000;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("GlasspaneHotkeys")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <returns>False if another app already owns this shortcut.</returns>
        public bool Register(uint modifiers, uint virtualKey, Action action)
        {
            int id = _nextId++;
            if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey)) return false;
            _actions[id] = action;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
            _source.Dispose();
        }
    }
}
