using System;
using System.Windows;
using Glasspane.Native;

namespace Glasspane.Core
{
    /// <summary>
    /// A self-contained feature (clipboard today; audio, VPN, phone, Claude chat later).
    /// The shell gives every widget its own glass window that can float or be pinned to
    /// the desktop, so a widget only has to provide its content.
    /// </summary>
    public interface IWidget : IDisposable
    {
        /// <summary>Stable id used to save the widget's position and settings, e.g. "clipboard".</summary>
        string Id { get; }

        string Title { get; }

        /// <summary>One line for the widget's tile in Settings.</summary>
        string Description => "";

        /// <summary>Default size for the first launch.</summary>
        Size DefaultSize { get; }

        /// <summary>Segoe Fluent Icons glyph shown next to the title.</summary>
        string Glyph => "";

        /// <summary>
        /// True if the widget should take up the spare height when sharing a window (e.g. a long list).
        /// False for compact widgets that are only as tall as their content (e.g. audio controls).
        /// </summary>
        bool FillsHeight => true;

        /// <summary>Builds the UI shown inside the widget's window.</summary>
        FrameworkElement CreateView();

        /// <summary>Called when the user brings the widget up with its shortcut or the tray.</summary>
        void OnSummoned() { }

        /// <param name="onDesktop">True when the widget is part of the desktop rather than a window.</param>
        /// <param name="active">True while the mouse is over it or it has keyboard focus.
        /// On the desktop, widgets should hide controls that aren't needed when inactive.</param>
        void OnPresentationChanged(bool onDesktop, bool active) { }
    }

    /// <summary>Shared services handed to every widget.</summary>
    public sealed class WidgetContext
    {
        public WidgetContext(string dataFolder, ForegroundTracker foreground)
        {
            DataFolder = dataFolder;
            Foreground = foreground;
        }

        /// <summary>%LOCALAPPDATA%\Glasspane. Widgets should use a subfolder named after their id.</summary>
        public string DataFolder { get; }

        /// <summary>The last app the user was in before clicking a widget.</summary>
        public ForegroundTracker Foreground { get; }

        public string FolderFor(IWidget widget) => System.IO.Path.Combine(DataFolder, widget.Id);
    }
}
