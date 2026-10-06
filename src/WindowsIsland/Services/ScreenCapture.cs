using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsIsland.Services;

/// <summary>Screenshots for "ask about the screen": the window you were using, saved as a PNG the chat can attach.</summary>
internal static class ScreenCapture
{
    public static string Folder => Path.Combine(App.DataDirectory, "attachments");

    /// <summary>Captures <paramref name="hwnd"/> (or, if it can't, the monitor under the cursor). Returns the file and a label.</summary>
    public static (string Path, string Label)? Capture(IntPtr hwnd)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            DeleteOldShots();
            string path = Path.Combine(Folder, $"tela-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd) && CaptureWindow(hwnd) is { } window)
            {
                using (window)
                    window.Save(path, ImageFormat.Png);
                string title = WindowTitle(hwnd);
                return (path, title.Length > 0 ? title : "Janela");
            }

            using var screen = CaptureMonitorUnderCursor();
            screen.Save(path, ImageFormat.Png);
            return (path, "Tela inteira");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window) || window.Width <= 0 || window.Height <= 0)
            return null;
        // The visible frame, without Windows 10/11's invisible resize borders.
        var frame = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) == 0 && bounds.Width > 0
            ? bounds
            : window;

        // PrintWindow draws the window itself, even if something (like the island) is on top of it.
        using var full = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppArgb);
        bool printed;
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            printed = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        var crop = new Rectangle(frame.Left - window.Left, frame.Top - window.Top, frame.Width, frame.Height);
        crop.Intersect(new Rectangle(0, 0, full.Width, full.Height));
        if (printed && crop.Width > 0 && crop.Height > 0)
        {
            var result = full.Clone(crop, PixelFormat.Format32bppArgb);
            if (!IsBlank(result))
                return result;
            result.Dispose();
        }

        // Some apps (games, protected video) render nothing for PrintWindow: copy what's on screen instead.
        var screen = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(screen))
            g.CopyFromScreen(frame.Left, frame.Top, 0, 0, new Size(frame.Width, frame.Height));
        return screen;
    }

    private static Bitmap CaptureMonitorUnderCursor()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        return bitmap;
    }

    /// <summary>All one color (typically black): the window didn't draw into PrintWindow.</summary>
    private static bool IsBlank(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(0, 0);
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 12; x++)
                if (bitmap.GetPixel(bitmap.Width * x / 12, bitmap.Height * y / 12) != first)
                    return false;
        return true;
    }

    /// <summary>Screenshots are only needed for the conversation at hand: keep a day's worth.</summary>
    private static void DeleteOldShots()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "tela-*.png"))
        {
            try
            {
                if (File.GetCreationTime(file) < DateTime.Now.AddDays(-1))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // In use: next time.
            }
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
