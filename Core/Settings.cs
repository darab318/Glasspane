using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glasspane.Core
{
    public enum WidgetMode
    {
        Window,
        Pinned
    }

    /// <summary>
    /// State of one glass window, which can hold one or more widgets.
    /// Positions are in WPF device-independent pixels.
    /// </summary>
    public sealed class WidgetSettings
    {
        /// <summary>Unique id of this window.</summary>
        public string Id { get; set; } = "";

        /// <summary>The widgets in this window, top to bottom.</summary>
        public List<string> WidgetIds { get; set; } = new();

        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public WidgetMode Mode { get; set; } = WidgetMode.Window;

        // Each mode keeps its own look.

        /// <summary>Window mode panel opacity: 0 = invisible, 1 = solid.</summary>
        public double BackgroundOpacity { get; set; } = 0.75;

        /// <summary>Window mode blur strength, 0 (none) to 1 (heavy).</summary>
        public double BlurStrength { get; set; }

        /// <summary>Desktop mode panel opacity. 0 = no panel at all, items sit on the wallpaper.</summary>
        public double DesktopOpacity { get; set; } = 0;

        /// <summary>Desktop mode blur strength, 0 (none) to 1 (heavy).</summary>
        public double DesktopBlurStrength { get; set; }

        /// <summary>Old on/off blur settings, read once and converted to strengths.</summary>
        public bool? Blur { get; set; }
        public bool? DesktopBlur { get; set; }

        /// <summary>When pinned, stops the widget being dragged or resized by accident.</summary>
        public bool Locked { get; set; }

        public bool Visible { get; set; } = true;
    }

    public sealed class AppSettings
    {
        /// <summary>Every glass window and which widgets it holds.</summary>
        public List<WidgetSettings> Windows { get; set; } = new();

        /// <summary>Old format (one window per widget, keyed by widget id). Read once, then migrated.</summary>
        public Dictionary<string, WidgetSettings>? Widgets { get; set; }

        /// <summary>Open the settings window (widget tiles) every time Glasspane starts.</summary>
        public bool ShowSettingsOnStart { get; set; } = true;

        /// <summary>Clipboard history size (pinned items are never removed).</summary>
        public int ClipboardHistoryLimit { get; set; } = 10000;

        /// <summary>Converts settings saved by the first version.</summary>
        public void Migrate()
        {
            if (Widgets != null)
            {
                foreach (var (widgetId, s) in Widgets)
                {
                    if (Windows.Any(w => w.WidgetIds.Contains(widgetId))) continue;
                    s.Id = widgetId;
                    s.WidgetIds = new List<string> { widgetId };
                    Windows.Add(s);
                }
                Widgets = null;
            }

            foreach (var w in Windows)
            {
                if (w.Blur is bool b) w.BlurStrength = b ? 0.5 : 0;
                if (w.DesktopBlur is bool d) w.DesktopBlurStrength = d ? 0.5 : 0;
                w.Blur = null;
                w.DesktopBlur = null;
            }
        }
    }

    public sealed class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _path;

        public SettingsStore(string dataFolder)
        {
            _path = Path.Combine(dataFolder, "settings.json");
            Current = Load();
        }

        public AppSettings Current { get; }

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
                    loaded.Migrate();
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings could not be read, using defaults: " + ex.Message);
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Settings could not be saved: " + ex.Message);
            }
        }
    }
}
