using System;
using System.IO;

namespace Glasspane.Core
{
    internal static class Log
    {
        private static readonly object Gate = new();
        private static string? _path;

        public static void Init(string dataFolder) => _path = Path.Combine(dataFolder, "log.txt");

        public static void Write(string message)
        {
            if (_path == null) return;
            try
            {
                lock (Gate)
                {
                    // Keep the log small
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > 1_000_000) info.Delete();
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never crash the app
            }
        }
    }
}
