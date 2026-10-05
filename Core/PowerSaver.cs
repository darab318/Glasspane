using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Glasspane.Core
{
    /// <summary>
    /// Keeps Glasspane light while you're not using it. When no widget is being hovered or
    /// used, the app switches to Windows "Efficiency mode" (the green leaf in Task Manager:
    /// lowest CPU priority and power-saving scheduling) and, after a while, hands unused
    /// memory back to Windows. The moment you touch a widget it switches back to full speed.
    /// </summary>
    internal static class PowerSaver
    {
        private static readonly HashSet<object> ActiveSources = new();
        private static readonly DispatcherTimer EnterTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private static readonly DispatcherTimer TrimTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        private static bool _efficient;

        static PowerSaver()
        {
            EnterTimer.Tick += (_, _) => { EnterTimer.Stop(); SetEfficient(true); TrimTimer.Start(); };
            TrimTimer.Tick += (_, _) => { TrimTimer.Stop(); TrimMemory(); };
        }

        /// <summary>Starts in efficiency mode; widgets wake it up when used.</summary>
        public static void Start() => EnterTimer.Start();

        /// <summary>A window reports whether it's currently being used (hovered or focused).</summary>
        public static void Report(object source, bool active)
        {
            bool changed = active ? ActiveSources.Add(source) : ActiveSources.Remove(source);
            if (!changed) return;

            if (ActiveSources.Count > 0)
            {
                EnterTimer.Stop();
                TrimTimer.Stop();
                SetEfficient(false);
            }
            else
            {
                EnterTimer.Stop();
                EnterTimer.Start();
            }
        }

        /// <summary>Full speed right now, e.g. when a shortcut brings a widget up.</summary>
        public static void Wake()
        {
            SetEfficient(false);
            if (ActiveSources.Count == 0)
            {
                EnterTimer.Stop();
                EnterTimer.Start();
            }
        }

        public static void Forget(object source) => Report(source, false);

        private static void SetEfficient(bool on)
        {
            if (on == _efficient) return;
            _efficient = on;
            try
            {
                using var me = Process.GetCurrentProcess();
                // Below normal rather than idle, so copying still gets recorded promptly while a game hogs the CPU
                me.PriorityClass = on ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;

                // EcoQoS (Windows 11, and Windows 10 2004+ for the throttling part)
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                };
                SetProcessInformation(me.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            }
            catch (Exception ex)
            {
                Log.Write("Efficiency mode change failed: " + ex.Message);
            }
        }

        /// <summary>
        /// After a while idle: tidy up the .NET heap (including big image buffers left over
        /// from copying screenshots) and return the memory Windows can have back.
        /// </summary>
        private static void TrimMemory()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                using var me = Process.GetCurrentProcess();
                EmptyWorkingSet(me.Handle);
            }
            catch (Exception ex)
            {
                Log.Write("Memory trim failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- Win32

        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
    }
}
