# Glasspane

Desktop widgets that can be a normal window or be pinned to your desktop, where they stop
being windows and become part of the desktop itself. Its first widget is an extended clipboard.

## Build and run

1. Double-click **Build.bat**.
   - If you don't have the .NET 8 SDK, it installs it for you with winget. Run Build.bat again afterwards.
2. When the build finishes, Glasspane starts automatically. The finished app is in the **App** folder (`App\Glasspane.exe`).

Glasspane then runs in the system tray.

## Settings and widgets

When Glasspane starts, the **Settings** window opens with a tile for each widget. Click a tile to turn
that widget on or off (lit up with a tick = on). Widgets added in an update start switched off, so turn on the ones you want here.
**Appearance for all widgets** sets Background and Blur for every widget at once. Pick
*On the desktop* or *As windows* first, since each mode has its own look. If widgets currently differ
the value shows *Mixed*; moving the slider makes them all the same. Each widget's own ⚙ menu
can still fine-tune it afterwards.
It also has **Start Glasspane with Windows** and **Show this window when Glasspane starts**.
Open it again any time from the tray icon (right-click → Settings and widgets), or by launching Glasspane again.

## Using it

| Action | How |
|---|---|
| Bring up the clipboard | **Ctrl+Alt+V** |
| Bring up all widgets that are on | Left-click the tray icon |
| Copy an item back | Click it |
| Paste into the app you were just using | Double-click, press **Enter**, or use the paste button on the card |
| Paste as plain text | **Shift+Enter**, or right-click → Copy as plain text |
| Pin an item so it's never deleted | Pin button on the card, or **Ctrl+P** |
| Delete an item | **Del**, or the bin button |
| Search | Type straight away after Ctrl+Alt+V |
| Move between search and list | **↓** / **↑** |

**Window ↔ desktop.** The pin button in the title bar switches modes. On the desktop, the panel,
border and buttons dissolve away and your clipboard items sit straight on the wallpaper, like desktop icons.
On the desktop the widget:
- has no taskbar button and doesn't show in Alt+Tab
- always stays underneath your other windows, even when you click it
- stays visible on Win+D / "Show desktop"
- shows its search, filters and buttons only while your mouse is over it
- can be locked so it can't be moved or resized by accident

If it's buried under windows, Ctrl+Alt+V lifts it to the front. It sinks back into the desktop when you click elsewhere.

**Appearance** (the ⚙ button). Window mode and desktop mode each remember their own settings:
- **Background**: from invisible (0%, the default on the desktop) to solid. While the wallpaper shows
  through, text gets a soft shadow so it stays readable, like desktop icon labels.
- **Blur**: from none to heavy. On the desktop the widget blurs its own copy of your wallpaper,
  so any strength is possible (it follows the widget as you move it and updates when the wallpaper
  changes). As a window, other windows may be behind it, so it uses Windows' own blur, which has a
  single strength: any value above 0 turns it on.

## Audio

- **Volume slider** for whatever you're listening on, with a live level meter underneath.
  Scroll the mouse wheel anywhere over the widget to nudge it up or down.
- **Mute**: the round button. Turning the volume up unmutes, like Windows does.
- **Switch output**: your speakers, headphones, monitor and so on appear as buttons. Click one
  and everything moves to it (games, music, calls and system sounds).
- **App volumes**: opens a slider and mute button for each app that's making sound.

It stays in sync if you change the volume elsewhere, e.g. with keyboard media keys or Windows' own menu.

## System

Live readings with a one-minute history graph for each:
- **CPU**: total usage. Details add a bar per core, processes, threads and up time.
- **Memory**: how much is in use. Details add available, committed and cached memory (as in Task Manager).
- **Disk**: the busiest disk's activity and total read/write speed. Details add each disk
  separately and free space on each drive.
- **GPU**: graphics usage (like Task Manager, the busiest engine). Details add dedicated and shared
  graphics memory and the main engines (3D, video decode, copy…).

The options button (top right of the widget) has a **Show** and a **Details** switch for each reading,
plus a switch for the history graphs (off = a simple bar). Readings that are off aren't measured at all.
It updates every second while your mouse is over it, every 2 seconds otherwise, and stops while hidden.
CPU and GPU temperatures aren't included: Windows doesn't provide them without extra drivers.

## Clock

A large clock with the date (options: seconds, 12/24-hour). Underneath, chips open three sections;
click an open chip again to close it:
- **Alarms**: type a time (07:30, 7.30 or 7:30pm), an optional label, and pick days to repeat (none = once).
  When an alarm goes off, a pop-up appears in the bottom-right corner on top of everything, with the
  Windows alarm sound, **Snooze 5 min** and **Dismiss**. Alarms work even while the clock widget is turned off.
- **Timer**: presets (1 min – 1 hour) or type your own (25 = 25 minutes, 1:30 = 1 min 30 s, 1:00:00 = 1 hour).
  Pause, resume, reset. Same pop-up when it finishes.
- **Stopwatch**: start/stop, laps, reset.

The clock redraws once a minute (once a second with seconds on); nothing ticks faster unless the
timer or stopwatch is open and running.

## Now Playing

The song or video that's playing in Spotify, YouTube (in your browser) and other apps, with album art,
progress (click to jump) and previous / play-pause / next. It uses Windows' own media controls, so it
updates only when the track or play state changes.

## Weather

Current conditions, the next 6 hours and the next 5 days, from Open-Meteo (free, no account). Set your
town in the ⚙ menu (it starts on London), plus °C/°F, mph/km/h, and whether to show the hours and days.
It downloads a few kilobytes every 30 minutes, and only while the widget is showing.

## Notes

Sticky notes that save as you type. Add more notes with **+**; switch between them with the chips.

## Screenshots & Downloads

Your newest screenshots (from Win + Print Screen, Snipping Tool and ShareX) and downloads.
- **Click** to copy (the picture itself for images), **drag** straight into Discord, a browser or a folder,
  **double-click** to open, **right-click** to open, show in folder or delete (to the Recycle Bin).
- Windows tells the shelf when files appear, so it never scans folders on a timer.

## Together or separate

Widgets can share one window or each have their own:
- **Split off**: in a shared window, hover over a widget's header and click the split button.
  It opens in its own window next to the original, with the same look and mode.
- **Put back together**: drag a window by its header onto another widget window. It lights up
  and says "Release to join"; let go and they merge.

Each window can be on the desktop or a normal window independently, and remembers its own position.

## What the clipboard keeps

- Text, including the formatting (HTML / RTF), so pasting back into Word or an email keeps the bold, links and so on
- Images: screenshots, copied pictures (saved as PNG, with transparency kept where the source app provides it)
- Files and folders copied in Explorer
- Which app each item came from, and when

History survives restarts. It keeps the newest 10,000 items; pinned items are never removed. To change the limit, edit `ClipboardHistoryLimit` in `settings.json`.

**Privacy:** items that password managers and Windows mark as private (the `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory` flags) are never recorded.

## Staying light

Glasspane is built to sit on your desktop all day without you noticing it:
- **Efficiency mode when idle.** A couple of seconds after you stop using a widget, the app
  switches itself to Windows' Efficiency mode (the green leaf in Task Manager): low CPU priority
  and power-saving scheduling. Hovering over a widget or pressing Ctrl+Alt+V switches it straight back.
- **Gives memory back.** After 30 seconds idle it tidies up and returns unused memory to Windows.
- **No work while idle.** Nothing polls in the background. Copying, volume changes and device
  changes arrive as notifications from Windows. The audio level meter and app-volume list only
  update while your mouse is over the widget, and the clipboard's "5 min ago" labels only
  update while they're visible.
- **Small previews.** Copied images are shown from small previews decoded at display size,
  not the full picture (a 4K screenshot uses ~0.4 MB instead of ~33 MB), and previews
  scrolled out of view can be freed.
- **Cheaper drawing.** Animations run at 30 fps and only when something actually changes.
- **Fast start.** The app is pre-compiled to native code, so there's no warm-up work at launch.

## Where data lives

`%LOCALAPPDATA%\Glasspane` (tray menu → Open data folder):
- `settings.json`: widget positions, modes and appearance
- `clipboard\clipboard.db`: history (SQLite)
- `clipboard\images\`: copied images
- `log.txt`: errors, useful if something misbehaves

## Backups and version control (GitHub)

- **First time:** double-click **GitHub-Setup.bat**. It installs Git and the GitHub tool if needed,
  has you sign in to GitHub in your browser, and uploads the project to a new **private**
  repository called `Glasspane` on your account.
- **After that:** double-click **Save-Version.bat** whenever you want a backup (for example after a
  new feature works). Type a short description and it's saved and uploaded.

Every saved version is kept on GitHub, so you can see what changed and go back to any of them.
Build output (`bin`, `obj`, `App`) isn't uploaded; it's rebuilt by Build.bat.

## How the code is organised

```
Core/        IWidget interface, settings, logging, start-with-Windows
Native/      Win32 interop: glass effect, hotkeys, foreground tracking, key presses
Shell/       WidgetWindow: a glass window holding one or more widgets (window/desktop modes)
             WidgetManager: splitting widgets off and joining windows
Themes/      Shared styles (buttons, chips, toggle switch, slider, scrollbars, menus)
Widgets/
  Clipboard/ Clipboard listener, capture, SQLite store, UI
  Audio/     Volume, level meter, output switching, per-app volumes (NAudio)
  SystemMonitor/ CPU, memory, disk and GPU readings (Windows performance counters)
  Time/      Clock, alarms, timer, stopwatch
  NowPlaying/ Media info and controls (Windows media session API)
  Weather/   Open-Meteo forecast
  Notes/     Sticky notes
  Shelf/     Screenshots and downloads
App.xaml.cs  Start-up, tray icon, global shortcut, widget registration
```

The app is a shell that hosts widgets. Every widget implements `IWidget` (an id, a title, a default size and a view)
and gets its own `WidgetWindow`, so it can be pinned, moved, made transparent and remembered on its own
without writing any of that again.

### Adding a future widget

1. Create `Widgets/<Name>/<Name>Widget.cs` implementing `IWidget`, plus a `UserControl` for its UI.
2. Register it in `App.OnStartup` with `_manager.Register(new <Name>Widget(context));`. It appears at the top of the first window, and can be split off from there.

The ideas so far, and the approach for each:

| Widget | Approach |
|---|---|
| Quick Claude chat | Anthropic Messages API with your own API key (billed separately from a claude.ai plan) |
| AirPods battery | Read Apple's Bluetooth LE battery broadcast (see AirPodsDesktop) |
| Phone | Android: KDE Connect protocol. iPhone: Bluetooth notification service (more limited) |
| Proton VPN | Proton's WireGuard config files plus the official WireGuard client |
