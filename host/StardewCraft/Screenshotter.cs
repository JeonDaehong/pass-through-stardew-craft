using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewCraft
{
    /// <summary>Saves a PNG of the game's area of the screen with ffmpeg's Desktop Duplication source.</summary>
    /// <remarks>
    /// Window capture (GDI or Windows Graphics Capture) returns stale frames while Windows presents a
    /// focused full-size game directly to the display, so this grabs what the monitor actually shows.
    /// </remarks>
    internal sealed class Screenshotter
    {
        private readonly ModConfig config;
        private readonly IMonitor monitor;

        public Screenshotter(ModConfig config, IMonitor monitor)
        {
            this.config = config;
            this.monitor = monitor;
        }

        /// <summary>Starts capturing the current frame; returns the target path, or null if it couldn't start.</summary>
        public string? Take()
        {
            if (!TryGetCaptureArea(out int output, out int x, out int y, out int width, out int height))
            {
                this.monitor.Log("Screenshot: can't find the game window.", LogLevel.Warn);
                return null;
            }

            string dir = Environment.ExpandEnvironmentVariables(this.config.ScreenshotDir);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"StardewCraft-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            // Grab a few frames and keep the last: the first duplicated frame can be empty.
            string source = $"ddagrab=output_idx={output}:framerate=30:dup_frames=1:offset_x={x}:offset_y={y}:video_size={width}x{height}";
            Task.Run(() =>
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    var (code, errors) = this.RunFfmpeg("-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", source,
                        "-frames:v", "5", "-update", "1", "-vf", "hwdownload,format=bgra", file);
                    if (code == 0 && File.Exists(file))
                    {
                        this.monitor.Log($"Screenshot saved: {file}", LogLevel.Info);
                        return;
                    }
                    // Display mode switches (e.g. alt-tab) make duplication fail once; just retry.
                    this.monitor.Log($"Screenshot attempt {attempt} failed ({code}): {errors.Trim()}", attempt == 2 ? LogLevel.Warn : LogLevel.Trace);
                }
            });
            return file;
        }

        private (int Code, string Errors) RunFfmpeg(params string[] args)
        {
            var start = new ProcessStartInfo(this.config.FfmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            try
            {
                using var ffmpeg = Process.Start(start)!;
                string errors = ffmpeg.StandardError.ReadToEnd();
                ffmpeg.WaitForExit();
                return (ffmpeg.ExitCode, errors);
            }
            catch (Exception ex)
            {
                return (-1, $"couldn't start ffmpeg ({this.config.FfmpegPath}): {ex.Message}");
            }
        }

        /// <summary>
        /// Finds the monitor showing the game (as a DXGI output index) and the game's client area relative to it.
        /// DXGI lists outputs in the same order Windows enumerates monitors, primary first on single-GPU systems.
        /// </summary>
        private static bool TryGetCaptureArea(out int output, out int x, out int y, out int width, out int height)
        {
            output = x = y = width = height = 0;
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out Rect client))
                return false;
            var origin = new PointStruct();
            if (!ClientToScreen(hwnd, ref origin))
                return false;

            IntPtr gameMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var monitors = new List<(IntPtr Handle, Rect Bounds, bool Primary)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(h, ref info))
                    monitors.Add((h, info.Monitor, (info.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            monitors.Sort((a, b) => b.Primary.CompareTo(a.Primary));

            int index = monitors.FindIndex(m => m.Handle == gameMonitor);
            if (index < 0)
                return false;
            Rect bounds = monitors[index].Bounds;

            // Clamp the client area to the monitor and keep even dimensions for encoders.
            int left = Math.Max(origin.X, bounds.Left);
            int top = Math.Max(origin.Y, bounds.Top);
            int right = Math.Min(origin.X + client.Right, bounds.Right);
            int bottom = Math.Min(origin.Y + client.Bottom, bounds.Bottom);
            output = index;
            x = left - bounds.Left;
            y = top - bounds.Top;
            width = (right - left) & ~1;
            height = (bottom - top) & ~1;
            return width > 0 && height > 0;
        }

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointStruct
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref PointStruct point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
