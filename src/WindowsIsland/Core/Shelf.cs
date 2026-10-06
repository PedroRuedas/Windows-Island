using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsIsland.Core;

/// <summary>
/// The island's shelf: files and folders dropped on it, kept (by reference, nothing is copied) until you drag them
/// somewhere else or remove them. Persisted in %LOCALAPPDATA%\WindowsIsland\shelf.json. UI thread only.
/// </summary>
public sealed class Shelf
{
    public const int MaxItems = 12;
    private static string FilePath => Path.Combine(App.DataDirectory, "shelf.json");

    private readonly List<string> _items;

    private Shelf(List<string> items) => _items = items;

    public IReadOnlyList<string> Items => _items;

    public event Action? Changed;

    public static Shelf Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } saved)
                return new Shelf(saved.Where(p => File.Exists(p) || Directory.Exists(p)).Take(MaxItems).ToList());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new Shelf(new List<string>());
    }

    /// <summary>Newest first; dropping something already on the shelf moves it to the front.</summary>
    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Reverse())
        {
            _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, path);
        }
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Remove(string path)
    {
        _items.Remove(path);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    /// <summary>Drops entries whose file was deleted or moved meanwhile.</summary>
    public void Prune()
    {
        if (_items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0)
            Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        Changed?.Invoke();
    }

    // ── Icons ──

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>A small preview for pictures, otherwise the icon Explorer shows for the file or folder.</summary>
    public static ImageSource? Thumbnail(string path)
    {
        try
        {
            if (Pictures.Contains(Path.GetExtension(path)) && File.Exists(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 96;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }

            var info = new SHFILEINFO();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
