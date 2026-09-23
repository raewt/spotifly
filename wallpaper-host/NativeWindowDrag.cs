using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spotifly.WallpaperHost;

internal static partial class NativeWindowDrag
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly object Sync = new();
    private static DragState? _activeDrag;

    public static bool Begin(int pointerX, int pointerY)
    {
        Process[] processes = Process.GetProcessesByName("Spotifly");
        try
        {
            nint window = processes
                .Select(process => process.MainWindowHandle)
                .FirstOrDefault(handle => handle != 0);
            if (window == 0 || !GetWindowRect(window, out Rect bounds)) return false;

            lock (Sync)
            {
                _activeDrag = new DragState(window, bounds.Left, bounds.Top, pointerX, pointerY);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    public static bool Move(int pointerX, int pointerY)
    {
        DragState? drag;
        lock (Sync) drag = _activeDrag;
        if (drag is null || !IsWindow(drag.Window)) return false;

        int x = drag.Left + pointerX - drag.PointerX;
        int y = drag.Top + pointerY - drag.PointerY;
        return SetWindowPos(drag.Window, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static void End()
    {
        lock (Sync) _activeDrag = null;
    }

    private sealed record DragState(nint Window, int Left, int Top, int PointerX, int PointerY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
