using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Glasspane.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Glasspane.Widgets.Audio
{
    /// <summary>An output device, as shown in the device list.</summary>
    public sealed class OutputDevice
    {
        public string Id { get; init; } = "";
        public string FullName { get; init; } = "";
        public string ShortName { get; init; } = "";
        public bool IsHeadphones { get; init; }
        public bool IsDefault { get; init; }
        public string Glyph => IsHeadphones ? "" : "";
    }

    /// <summary>One app's volume (all of its audio streams together).</summary>
    public sealed class AppVolume : INotifyPropertyChanged
    {
        private readonly List<AudioSessionControl> _sessions = new();
        private double _volume;
        private bool _muted;
        private bool _applying;
        private readonly object _writeGate = new();
        private bool _writing;
        private bool _dirty;

        public AppVolume(string name) => Name = name;

        public string Name { get; }

        public void Add(AudioSessionControl session) => _sessions.Add(session);

        /// <summary>0–100. Setting it changes the app's volume.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 100);
                Changed(nameof(Volume));
                if (!_applying) QueueWrite();
            }
        }

        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                Changed(nameof(Muted));
                Changed(nameof(MuteGlyph));
                if (!_applying) QueueWrite();
            }
        }

        public string MuteGlyph => Muted ? "" : "";

        /// <summary>
        /// Sends the latest volume/mute to Windows on a background thread. While a slider is being
        /// dragged, only the newest value is sent, so changes never pile up or arrive out of order.
        /// </summary>
        private void QueueWrite()
        {
            lock (_writeGate)
            {
                _dirty = true;
                if (_writing) return;
                _writing = true;
            }

            Task.Run(() =>
            {
                while (true)
                {
                    float volume;
                    bool muted;
                    lock (_writeGate)
                    {
                        if (!_dirty) { _writing = false; return; }
                        _dirty = false;
                        volume = (float)(_volume / 100.0);
                        muted = _muted;
                    }
                    foreach (var s in _sessions)
                    {
                        try
                        {
                            s.SimpleAudioVolume.Volume = volume;
                            s.SimpleAudioVolume.Mute = muted;
                        }
                        catch
                        {
                            // app closed
                        }
                    }
                }
            });
        }

        /// <summary>Reads the current values from Windows without writing them back.</summary>
        public void Read()
        {
            if (_sessions.Count == 0) return;
            _applying = true;
            try
            {
                Volume = Math.Round(_sessions[0].SimpleAudioVolume.Volume * 100);
                Muted = _sessions[0].SimpleAudioVolume.Mute;
            }
            catch
            {
                // app closed
            }
            finally
            {
                _applying = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Wraps Windows Core Audio: the default output device's volume, mute and level, the list of
    /// outputs, switching between them, and per-app volumes. Change notifications from Windows
    /// arrive on background threads and are passed to the UI thread.
    /// </summary>
    public sealed class AudioService : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private MMDevice? _device;

        /// <summary>The list of outputs or the default output changed.</summary>
        public event Action? DevicesChanged;

        /// <summary>Volume or mute changed (from here, the keyboard, or anywhere else).</summary>
        public event Action? VolumeChanged;

        public AudioService()
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            OpenDefaultDevice();
        }

        public bool HasDevice => _device != null;
        public string DeviceName => _device?.FriendlyName ?? "No output device";

        public double Volume
        {
            get { try { return _device == null ? 0 : _device.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0; } catch { return 0; } }
            set { try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(Math.Clamp(value, 0, 100) / 100.0); } catch { } }
        }

        public bool Muted
        {
            get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
            set { try { if (_device != null) _device.AudioEndpointVolume.Mute = value; } catch { } }
        }

        /// <summary>Current output level, 0–1, for the level meter.</summary>
        public double Peak
        {
            get { try { return _device?.AudioMeterInformation.MasterPeakValue ?? 0; } catch { return 0; } }
        }

        private void OpenDefaultDevice()
        {
            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                _device.Dispose();
                _device = null;
            }

            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
            catch (COMException)
            {
                _device = null; // no output devices at all
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data) =>
            _dispatcher.InvokeAsync(() => VolumeChanged?.Invoke());

        public List<OutputDevice> GetOutputs()
        {
            var list = new List<OutputDevice>();
            string? defaultId = _device?.ID;
            try
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (d)
                    {
                        string name = d.FriendlyName;
                        list.Add(new OutputDevice
                        {
                            Id = d.ID,
                            FullName = name,
                            ShortName = Shorten(name),
                            IsHeadphones = LooksLikeHeadphones(name),
                            IsDefault = d.ID == defaultId
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Could not list audio devices: " + ex.Message);
            }
            return list;
        }

        public bool SetDefault(string deviceId) => PolicyConfig.SetDefaultDevice(deviceId);

        private MMDeviceEnumerator? _appsEnumerator;
        private MMDevice? _appsDevice;

        /// <summary>
        /// Apps using the default output. Read on a background (MTA) thread, because Windows'
        /// audio session API is unreliable when used from the UI thread.
        /// </summary>
        public Task<List<AppVolume>> GetAppsAsync() => Task.Run(() =>
        {
            var apps = new Dictionary<string, AppVolume>(StringComparer.OrdinalIgnoreCase);
            try
            {
                _appsEnumerator ??= new MMDeviceEnumerator();
                var old = _appsDevice;
                _appsDevice = _appsEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                old?.Dispose();

                var manager = _appsDevice.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    string name = NameOf(session);
                    if (!apps.TryGetValue(name, out var app))
                    {
                        app = new AppVolume(name);
                        apps[name] = app;
                    }
                    app.Add(session);
                }
                foreach (var app in apps.Values) app.Read();
            }
            catch (Exception ex)
            {
                Log.Write("Could not read app volumes: " + ex.Message);
            }
            return apps.Values.OrderBy(a => a.Name == "System sounds" ? 1 : 0).ThenBy(a => a.Name).ToList();
        });

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> NameCache = new();

        private static string NameOf(AudioSessionControl s)
        {
            try
            {
                if (s.IsSystemSoundsSession) return "System sounds";
                uint pid = s.GetProcessID;
                if (pid != 0) return NameCache.GetOrAdd(pid, ProcessName);
                string display = s.DisplayName;
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@")) return display;
            }
            catch
            {
                // process gone
            }
            return "Unknown app";
        }

        private static string ProcessName(uint pid)
        {
            try
            {
                {
                    using var p = Process.GetProcessById((int)pid);
                    try
                    {
                        string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 32) return desc.Trim();
                    }
                    catch
                    {
                        // protected process
                    }
                    return p.ProcessName;
                }
            }
            catch
            {
                // process gone
            }
            return "Unknown app";
        }

        /// <summary>"Headphones (AirPods Pro)" → "AirPods Pro", "Speakers (Realtek Audio)" → "Speakers".</summary>
        private static string Shorten(string name)
        {
            int open = name.IndexOf(" (", StringComparison.Ordinal);
            if (open <= 0 || !name.EndsWith(")")) return name;
            string main = name.Substring(0, open).Trim();
            string detail = name.Substring(open + 2, name.Length - open - 3).Trim();
            string[] generic = { "Headphones", "Headset", "Headset Earphone", "Earphones", "Speakers", "Speaker" };
            bool mainIsGeneric = generic.Any(g => main.Equals(g, StringComparison.OrdinalIgnoreCase));
            bool detailIsDriver = new[] { "Realtek", "High Definition", "USB Audio", "Audio Device", "NVIDIA", "AMD", "Intel" }
                .Any(k => detail.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (mainIsGeneric && !detailIsDriver) return detail;
            return main;
        }

        private static bool LooksLikeHeadphones(string name) =>
            new[] { "headphone", "headset", "airpods", "buds", "earphone", "earbuds", "hands-free" }
                .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- Windows notifications (background threads)

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Post(reopen: false);
        public void OnDeviceAdded(string pwstrDeviceId) => Post(reopen: false);
        public void OnDeviceRemoved(string deviceId) => Post(reopen: false);
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) Post(reopen: true);
        }

        private void Post(bool reopen) => _dispatcher.InvokeAsync(() =>
        {
            if (reopen) OpenDefaultDevice();
            DevicesChanged?.Invoke();
            if (reopen) VolumeChanged?.Invoke();
        });

        public void Dispose()
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                _device.Dispose();
            }
            _enumerator.Dispose();
            _appsDevice?.Dispose();
            _appsEnumerator?.Dispose();
        }
    }
}
