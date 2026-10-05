using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Native
{
    internal static class KeyboardSender
    {
        /// <summary>Brings a window to the front and presses Ctrl+V in it.</summary>
        public static async Task<bool> PasteIntoAsync(IntPtr target)
        {
            if (target == IntPtr.Zero || !IsWindow(target)) return false;

            SetForegroundWindow(target);
            await Task.Delay(90); // let the target window take focus

            // If the user is still holding modifiers (e.g. from a hotkey), release them first
            var inputs = new System.Collections.Generic.List<INPUT>();
            foreach (var vk in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) inputs.Add(Key(vk, up: true));

            inputs.Add(Key(VK_CONTROL, up: false));
            inputs.Add(Key(VK_V, up: false));
            inputs.Add(Key(VK_V, up: true));
            inputs.Add(Key(VK_CONTROL, up: true));

            var arr = inputs.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) == arr.Length;
        }

        private static INPUT Key(ushort vk, bool up) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
        };
    }
}
