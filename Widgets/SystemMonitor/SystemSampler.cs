using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Glasspane.Core;
using Microsoft.Win32;

namespace Glasspane.Widgets.SystemMonitor
{
    /// <summary>One reading of everything the widget shows.</summary>
    public sealed class SystemSample
    {
        // CPU
        public double CpuPercent;
        public double[] CorePercents = Array.Empty<double>();
        public int ProcessCount, ThreadCount;

        // Memory (bytes)
        public double MemoryPercent;
        public ulong MemoryTotal, MemoryAvailable, CommitUsed, CommitLimit, Cached;

        // Disk
        public double DiskPercent;          // busiest disk's active time, like Task Manager
        public double DiskReadBps, DiskWriteBps;
        public List<DiskReading> Disks = new();

        // GPU
        public bool GpuAvailable;
        public double GpuPercent;
        public ulong GpuDedicatedUsed, GpuSharedUsed;
        public List<(string engine, double percent)> GpuEngines = new();
    }

    public sealed class DiskReading
    {
        public string Name = "";
        public double ActivePercent, ReadBps, WriteBps;
    }

    /// <summary>
    /// Reads CPU, memory, disk and GPU usage from Windows' own performance counters (the same
    /// sources Task Manager uses). Only the parts that are switched on are read, and the
    /// detailed parts only when details are showing, so a hidden or simple view costs less.
    /// </summary>
    public sealed class SystemSampler : IDisposable
    {
        private readonly object _gate = new();

        // CPU
        private long _prevIdle, _prevKernel, _prevUser;
        private long[] _prevCoreIdle = Array.Empty<long>(), _prevCoreTotal = Array.Empty<long>();

        // PDH (disk, GPU)
        private IntPtr _diskQuery, _diskIdle, _diskRead, _diskWrite;
        private IntPtr _gpuQuery, _gpuEngine, _gpuDedicated, _gpuShared;

        public SystemSampler()
        {
            CpuName = ReadCpuName();
            (GpuName, GpuDedicatedTotal) = ReadGpuInfo();
            ThreadsTotal = Environment.ProcessorCount;
        }

        public string CpuName { get; }
        public int ThreadsTotal { get; }
        public string GpuName { get; }
        public ulong GpuDedicatedTotal { get; }

        /// <summary>Takes a reading. Safe to call from a background thread.</summary>
        public SystemSample Sample(SystemOptions o)
        {
            lock (_gate)
            {
                var s = new SystemSample();
                try { if (o.ShowCpu) ReadCpu(s, o.CpuDetail); } catch (Exception ex) { Log.Write("CPU reading failed: " + ex.Message); }
                try { if (o.ShowMemory) ReadMemory(s); } catch (Exception ex) { Log.Write("Memory reading failed: " + ex.Message); }
                try { if (o.ShowDisk) ReadDisk(s, o.DiskDetail); else CloseDisk(); } catch (Exception ex) { Log.Write("Disk reading failed: " + ex.Message); }
                try { if (o.ShowGpu) ReadGpu(s, o.GpuDetail); else CloseGpu(); } catch (Exception ex) { Log.Write("GPU reading failed: " + ex.Message); }
                return s;
            }
        }

        // ---------------------------------------------------------------- CPU

        private void ReadCpu(SystemSample s, bool detail)
        {
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                long dIdle = idle - _prevIdle, dTotal = (kernel - _prevKernel) + (user - _prevUser);
                if (_prevKernel != 0 && dTotal > 0) s.CpuPercent = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            }

            if (GetPerformanceInfo(out var perf, Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            {
                s.ProcessCount = (int)perf.ProcessCount;
                s.ThreadCount = (int)perf.ThreadCount;
            }

            if (detail) s.CorePercents = ReadCores();
        }

        /// <summary>Per-core usage, from the kernel's per-processor time counters.</summary>
        private double[] ReadCores()
        {
            int count = Environment.ProcessorCount;
            int itemSize = 48; // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
            IntPtr buffer = Marshal.AllocHGlobal(itemSize * count);
            try
            {
                if (NtQuerySystemInformation(8, buffer, itemSize * count, out int returned) != 0) return Array.Empty<double>();
                count = Math.Min(count, returned / itemSize);

                if (_prevCoreIdle.Length != count)
                {
                    _prevCoreIdle = new long[count];
                    _prevCoreTotal = new long[count];
                }

                var result = new double[count];
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buffer + i * itemSize;
                    long idle = Marshal.ReadInt64(p, 0);
                    long kernel = Marshal.ReadInt64(p, 8); // includes idle
                    long user = Marshal.ReadInt64(p, 16);
                    long total = kernel + user;
                    long dIdle = idle - _prevCoreIdle[i], dTotal = total - _prevCoreTotal[i];
                    result[i] = _prevCoreTotal[i] != 0 && dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
                    _prevCoreIdle[i] = idle;
                    _prevCoreTotal[i] = total;
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadCpuName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
            }
            catch
            {
                return "Processor";
            }
        }

        // ---------------------------------------------------------------- memory

        private static void ReadMemory(SystemSample s)
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                s.MemoryTotal = status.ullTotalPhys;
                s.MemoryAvailable = status.ullAvailPhys;
                s.MemoryPercent = status.ullTotalPhys == 0 ? 0 : 100.0 * (status.ullTotalPhys - status.ullAvailPhys) / status.ullTotalPhys;
            }
            if (GetPerformanceInfo(out var perf, Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            {
                ulong page = (ulong)perf.PageSize;
                s.CommitUsed = (ulong)perf.CommitTotal * page;
                s.CommitLimit = (ulong)perf.CommitLimit * page;
                s.Cached = (ulong)perf.SystemCache * page;
            }
        }

        // ---------------------------------------------------------------- disk

        private void ReadDisk(SystemSample s, bool detail)
        {
            if (_diskQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _diskQuery) != 0) { _diskQuery = IntPtr.Zero; return; }
                PdhAddEnglishCounter(_diskQuery, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out _diskIdle);
                PdhAddEnglishCounter(_diskQuery, @"\PhysicalDisk(*)\Disk Read Bytes/sec", IntPtr.Zero, out _diskRead);
                PdhAddEnglishCounter(_diskQuery, @"\PhysicalDisk(*)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWrite);
            }
            if (PdhCollectQueryData(_diskQuery) != 0) return;

            var idle = ReadArray(_diskIdle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100);
            var read = ReadArray(_diskRead, PDH_FMT_DOUBLE).ToDictionary(x => x.name, x => x.value);
            var write = ReadArray(_diskWrite, PDH_FMT_DOUBLE).ToDictionary(x => x.name, x => x.value);

            foreach (var (name, idlePct) in idle)
            {
                read.TryGetValue(name, out double r);
                write.TryGetValue(name, out double w);
                if (name == "_Total")
                {
                    s.DiskReadBps = r;
                    s.DiskWriteBps = w;
                    continue;
                }
                double active = Math.Clamp(100 - idlePct, 0, 100);
                s.DiskPercent = Math.Max(s.DiskPercent, active);
                if (detail) s.Disks.Add(new DiskReading { Name = DiskLabel(name), ActivePercent = active, ReadBps = r, WriteBps = w });
            }
            s.Disks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        /// <summary>"0 C: D:" → "Disk 0 (C: D:)".</summary>
        private static string DiskLabel(string instance)
        {
            int space = instance.IndexOf(' ');
            if (space < 0) return "Disk " + instance;
            return $"Disk {instance.Substring(0, space)} ({instance.Substring(space + 1).Trim()})";
        }

        private void CloseDisk()
        {
            if (_diskQuery == IntPtr.Zero) return;
            PdhCloseQuery(_diskQuery);
            _diskQuery = IntPtr.Zero;
        }

        // ---------------------------------------------------------------- GPU

        private void ReadGpu(SystemSample s, bool detail)
        {
            if (_gpuQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0) { _gpuQuery = IntPtr.Zero; return; }
                if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuEngine) != 0)
                {
                    CloseGpu(); // GPU counters need Windows 10 1709+ and a WDDM 2 driver
                    return;
                }
                PdhAddEnglishCounter(_gpuQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _gpuDedicated);
                PdhAddEnglishCounter(_gpuQuery, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _gpuShared);
            }
            if (PdhCollectQueryData(_gpuQuery) != 0) return;
            s.GpuAvailable = true;

            // Instance names look like "pid_1234_luid_0x0000_0xC2B6_phys_0_eng_0_engtype_3D".
            // Like Task Manager: add up each engine type per GPU, and the busiest type is the GPU's usage.
            var perAdapter = new Dictionary<string, Dictionary<string, double>>();
            foreach (var (name, value) in ReadArray(_gpuEngine, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100))
            {
                string luid = Between(name, "luid_", "_phys");
                string type = After(name, "engtype_");
                if (luid.Length == 0 || type.Length == 0) continue;
                if (!perAdapter.TryGetValue(luid, out var types)) perAdapter[luid] = types = new Dictionary<string, double>();
                types[type] = types.GetValueOrDefault(type) + value;
            }

            string? mainLuid = null;
            foreach (var (luid, types) in perAdapter)
            {
                double busiest = types.Values.DefaultIfEmpty(0).Max();
                if (mainLuid == null || busiest > s.GpuPercent)
                {
                    mainLuid = luid;
                    s.GpuPercent = Math.Min(100, busiest);
                }
            }

            if (!detail) return;

            if (mainLuid != null)
                s.GpuEngines = perAdapter[mainLuid]
                    .Select(kv => (Friendly(kv.Key), Math.Min(100, kv.Value)))
                    .Where(x => x.Item2 >= 0.5 || x.Item1 is "3D" or "Video decode" or "Copy")
                    .OrderByDescending(x => x.Item2)
                    .Take(4)
                    .ToList();

            // Memory: the adapter with the most dedicated memory in use is the one doing the work
            s.GpuDedicatedUsed = (ulong)ReadArray(_gpuDedicated, PDH_FMT_LARGE_AS_DOUBLE).Select(x => x.value).DefaultIfEmpty(0).Max();
            s.GpuSharedUsed = (ulong)ReadArray(_gpuShared, PDH_FMT_LARGE_AS_DOUBLE).Select(x => x.value).DefaultIfEmpty(0).Max();
        }

        private void CloseGpu()
        {
            if (_gpuQuery == IntPtr.Zero) return;
            PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
        }

        private static string Friendly(string engineType) => engineType switch
        {
            "3D" => "3D",
            "Copy" => "Copy",
            "VideoDecode" => "Video decode",
            "VideoEncode" => "Video encode",
            "VideoProcessing" => "Video processing",
            "Compute_0" or "Compute_1" or "Compute" => "Compute",
            _ => engineType.Replace('_', ' ')
        };

        private static string Between(string s, string start, string end)
        {
            int a = s.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            a += start.Length;
            int b = s.IndexOf(end, a, StringComparison.Ordinal);
            return b < 0 ? "" : s.Substring(a, b - a);
        }

        private static string After(string s, string start)
        {
            int a = s.IndexOf(start, StringComparison.Ordinal);
            return a < 0 ? "" : s.Substring(a + start.Length);
        }

        /// <summary>Graphics card name and memory size, from the display driver's registry entry.</summary>
        private static (string name, ulong memory) ReadGpuInfo()
        {
            string best = "Graphics";
            ulong bestMemory = 0;
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return (best, 0);
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using var key = cls.OpenSubKey(sub);
                        if (key == null) continue;
                        ulong memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                        {
                            long l => (ulong)l,
                            byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                            int i => (uint)i,
                            _ => 0UL
                        };
                        if (memory > bestMemory)
                        {
                            bestMemory = memory;
                            best = (key.GetValue("DriverDesc") as string)?.Trim() ?? best;
                        }
                    }
                    catch
                    {
                        // a key we're not allowed to read; skip it
                    }
                }
            }
            catch
            {
                // no access to the registry key
            }
            return (best, bestMemory);
        }

        // ---------------------------------------------------------------- PDH helpers

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_LARGE_AS_DOUBLE = 0x00000200; // memory counters read fine as doubles
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        /// <summary>Reads every instance of a wildcard counter as (instance name, value).</summary>
        private static List<(string name, double value)> ReadArray(IntPtr counter, uint format)
        {
            var list = new List<(string, double)>();
            if (counter == IntPtr.Zero) return list;

            uint size = 0;
            int r = PdhGetFormattedCounterArray(counter, format, ref size, out uint count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return list;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, format, ref size, out count, buffer) != 0) return list;
                // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit: name pointer (8), status (4) + padding (4), value (8)
                const int itemSize = 24;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * itemSize;
                    int status = Marshal.ReadInt32(item, 8);
                    if (status != 0 && status != 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    list.Add((name, value));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return list;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                CloseDisk();
                CloseGpu();
            }
        }

        // ---------------------------------------------------------------- Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")]
        private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);
    }
}
