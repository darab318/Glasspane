using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using Glasspane.Core;
using Glasspane.Native;
using Glasspane.Shell;
using Glasspane.Widgets.Audio;
using Glasspane.Widgets.Clipboard;
using WinForms = System.Windows.Forms;

namespace Glasspane
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private EventWaitHandle? _showSignal;
        private SettingsStore _settings = null!;
        private ForegroundTracker _foreground = null!;
        private HotkeyManager _hotkeys = null!;
        private WidgetManager _manager = null!;
        private WinForms.NotifyIcon? _tray;
        private SettingsWindow? _settingsWindow;

        public static string DataFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glasspane");

        protected override void OnStartup(StartupEventArgs e)
        {
            // One copy at a time. Launching it again just brings the clipboard up.
            _singleInstance = new Mutex(true, "Glasspane.SingleInstance", out bool isFirst);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Glasspane.Show");
            if (!isFirst)
            {
                _showSignal.Set();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Animations at 30 fps instead of 60. Fades and slides look the same, but each frame of
            // a transparent window is a full redraw, so this halves the cost of every animation.
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 30 });

            Directory.CreateDirectory(DataFolder);
            Log.Init(DataFolder);
            Log.Write("Starting Glasspane");

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Write("Unhandled: " + args.Exception);
                args.Handled = true; // a widget glitch shouldn't take the whole app down
            };

            _settings = new SettingsStore(DataFolder);
            _foreground = new ForegroundTracker();
            var context = new WidgetContext(DataFolder, _foreground);

            // ---- Widgets. Future ones (VPN, phone, Claude) get registered here the same way.
            _manager = new WidgetManager(_settings);
            _manager.Register(new ClipboardWidget(context, _settings.Current.ClipboardHistoryLimit));
            TryRegister(() => new AudioWidget());
            _manager.LayoutChanged += (_, _) => RefreshTrayMenu();
            _manager.Start();

            // ---- Settings window with a tile per widget, shown at start-up unless turned off
            _settingsWindow = new SettingsWindow(_manager, _settings);
            if (_settings.Current.ShowSettingsOnStart) _settingsWindow.Open();

            // ---- Global shortcut: Ctrl+Alt+V brings up the clipboard
            _hotkeys = new HotkeyManager();
            if (!_hotkeys.Register(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_V,
                    () => _manager.Summon("clipboard")))
                Log.Write("Ctrl+Alt+V is already used by another app");

            ListenForSecondLaunch();
            CreateTrayIcon();

            // Drop into low-power mode until a widget is actually used
            PowerSaver.Start();
        }

        /// <summary>A widget that fails to start (e.g. no audio hardware) shouldn't stop the others.</summary>
        private void TryRegister(Func<IWidget> create)
        {
            try
            {
                _manager.Register(create());
            }
            catch (Exception ex)
            {
                Log.Write("Widget failed to start: " + ex);
            }
        }

        private void ListenForSecondLaunch()
        {
            var thread = new Thread(() =>
            {
                while (_showSignal!.WaitOne())
                    Dispatcher.InvokeAsync(() => { _settingsWindow?.Open(); }); // launching it again opens Settings
            })
            { IsBackground = true, Name = "SecondLaunchListener" };
            thread.Start();
        }

        // ---------------------------------------------------------------- tray

        private void CreateTrayIcon()
        {
            System.Drawing.Icon icon;
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                icon = res != null ? new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
            }
            catch
            {
                icon = System.Drawing.SystemIcons.Application;
            }

            _tray = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = "Glasspane – Ctrl+Alt+V for clipboard",
                Visible = true,
                ContextMenuStrip = new WinForms.ContextMenuStrip()
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button != WinForms.MouseButtons.Left) return;
                // Bring up the widgets that are on; if none are, open Settings to pick some
                var visible = _manager.Windows.Where(x => x.IsVisible).ToList();
                if (visible.Count == 0) _settingsWindow?.Open();
                else foreach (var w in visible) w.Summon();
            };
            _tray.ContextMenuStrip.Opening += (_, _) => RefreshTrayMenu();
            RefreshTrayMenu();
        }

        private void RefreshTrayMenu()
        {
            if (_tray?.ContextMenuStrip == null) return;
            var menu = _tray.ContextMenuStrip;
            menu.Items.Clear();

            menu.Items.Add(new WinForms.ToolStripMenuItem("Settings and widgets…", null, (_, _) => _settingsWindow?.Open())
            {
                Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold)
            });
            menu.Items.Add(new WinForms.ToolStripSeparator());

            foreach (var w in _manager.Windows.Where(x => x.IsVisible))
            {
                var window = w;
                menu.Items.Add(new WinForms.ToolStripMenuItem($"Show {window.DisplayTitle}", null, (_, _) => window.Summon()));
                menu.Items.Add(new WinForms.ToolStripMenuItem(window.IsPinned ? "    Make it a window" : "    Put on desktop", null,
                    (_, _) => { window.Summon(); window.SetPinned(!window.IsPinned); }));
            }

            menu.Items.Add(new WinForms.ToolStripSeparator());

            var startup = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = SafeIsStartupEnabled() };
            startup.Click += (_, _) =>
            {
                try { StartupManager.SetEnabled(!startup.Checked); }
                catch (Exception ex) { Log.Write("Startup toggle failed: " + ex.Message); }
            };
            menu.Items.Add(startup);

            menu.Items.Add(new WinForms.ToolStripMenuItem("Open data folder", null,
                (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true })));

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("Quit Glasspane", null, (_, _) => Quit()));
        }

        private static bool SafeIsStartupEnabled()
        {
            try { return StartupManager.IsEnabled; } catch { return false; }
        }

        private void Quit()
        {
            _settingsWindow?.CloseForExit();
            _manager.CloseAll();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _hotkeys?.Dispose();
            _foreground?.Dispose();
            if (_singleInstance != null)
            {
                try { _singleInstance.ReleaseMutex(); } catch { /* not the owner (second launch) */ }
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }
    }
}
