using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using static Glasspane.Native.NativeMethods;

namespace Glasspane.Native
{
    /// <summary>
    /// Glass effects. Uses the window "accent policy", which (unlike the Windows 11
    /// Mica/Acrylic system backdrops) keeps blurring when the window is inactive,
    /// which matters for something that sits on the desktop all day.
    /// </summary>
    internal static class Backdrop
    {
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_DONOTROUND = 1;

        /// <summary>Acrylic accent arrived in Windows 10 1803 (build 17134).</summary>
        public static bool SupportsAcrylic => Environment.OSVersion.Version.Build >= 17134;

        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        /// <param name="opacity">0 = fully see-through, 1 = solid tint.</param>
        /// <param name="lowQuality">Plain blur instead of acrylic (acrylic lags while dragging).</param>
        /// <returns>True if the OS could draw the tint itself; false means the caller should draw it.</returns>
        public static bool Apply(IntPtr hwnd, bool blur, double opacity, Color tint, bool lowQuality = false)
        {
            if (hwnd == IntPtr.Zero) return true;
            opacity = Math.Clamp(opacity, 0, 1);
            byte alpha = (byte)Math.Round(opacity * 255);

            int state;
            bool osDrawsTint = true;
            if (!blur)
            {
                state = ACCENT_ENABLE_TRANSPARENTGRADIENT;
            }
            else if (SupportsAcrylic && !lowQuality)
            {
                state = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                if (alpha == 0) alpha = 1; // alpha 0 renders black on some builds
            }
            else
            {
                state = ACCENT_ENABLE_BLURBEHIND; // cannot tint
                osDrawsTint = false;
            }

            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2, // use GradientColor
                GradientColor = ((uint)alpha << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R
            };
            SetAccent(hwnd, policy);
            return osDrawsTint;
        }

        /// <summary>
        /// Plain blur of whatever is behind the window, with no tint (the app draws its own tint).
        /// Works with per-pixel transparent (layered) windows.
        /// </summary>
        public static void SetBlur(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            SetAccent(hwnd, new AccentPolicy { AccentState = on ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED });
        }

        public static void Disable(IntPtr hwnd) =>
            SetAccent(hwnd, new AccentPolicy { AccentState = ACCENT_DISABLED });

        private static void SetAccent(IntPtr hwnd, AccentPolicy policy)
        {
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static void SetDarkFrame(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }

        public static void SetRoundedCorners(IntPtr hwnd, bool round)
        {
            if (!IsWindows11) return;
            int pref = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
    }
}
