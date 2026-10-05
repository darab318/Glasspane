using System;
using System.IO;
using System.Text.Json;
using Glasspane.Core;

namespace Glasspane.Widgets.SystemMonitor
{
    /// <summary>Which readings the system widget shows, and how much detail. Saved in the widget's own folder.</summary>
    public sealed class SystemOptions
    {
        public bool ShowCpu { get; set; } = true;
        public bool CpuDetail { get; set; }
        public bool ShowMemory { get; set; } = true;
        public bool MemoryDetail { get; set; }
        public bool ShowDisk { get; set; } = true;
        public bool DiskDetail { get; set; }
        public bool ShowGpu { get; set; } = true;
        public bool GpuDetail { get; set; }

        /// <summary>Small history graph under each reading.</summary>
        public bool ShowGraphs { get; set; } = true;

        public SystemOptions Clone() => (SystemOptions)MemberwiseClone();

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static SystemOptions Load(string folder)
        {
            try
            {
                string path = Path.Combine(folder, "options.json");
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<SystemOptions>(File.ReadAllText(path)) ?? new SystemOptions();
            }
            catch (Exception ex)
            {
                Log.Write("System widget options could not be read: " + ex.Message);
            }
            return new SystemOptions();
        }

        public void Save(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "options.json"), JsonSerializer.Serialize(this, Json));
            }
            catch (Exception ex)
            {
                Log.Write("System widget options could not be saved: " + ex.Message);
            }
        }
    }
}
