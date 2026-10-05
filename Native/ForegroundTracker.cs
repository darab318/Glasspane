using System;
using System.Diagnostics;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Native
{
    /// <summary>
    /// Remembers the last window you were using outside Glasspane, so items can be
    /// pasted straight back into it.
    /// </summary>
    public sealed class ForegroundTracker : IDisposable
    {
        private readonly WinEventDelegate _callback; // kept alive so the GC doesn't collect it
        private readonly IntPtr _hook;
        private readonly uint _ownPid = (uint)Environment.ProcessId;

        public IntPtr LastExternalWindow { get; private set; }

        public ForegroundTracker()
        {
            _callback = OnForegroundChanged;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            Remember(GetForegroundWindow());
        }

        private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            => Remember(hwnd);

        private void Remember(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return;

            // Clicking the desktop or taskbar shouldn't become the paste target
            string cls = GetClassNameOf(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;

            LastExternalWindow = hwnd;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        }
    }
}
