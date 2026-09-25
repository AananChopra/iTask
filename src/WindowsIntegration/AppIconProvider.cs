using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iTask.Utilities;

namespace iTask.WindowsIntegration;

/// <summary>
/// Large (256 px) app icons from the shell — the same source Start uses — so dock icons stay sharp
/// when magnified. Tries the app's AppUserModelID (shell:AppsFolder), then its executable.
/// </summary>
public static class AppIconProvider
{
    private const int Size = 256;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? appUserModelId, string? exePath, uint? processId)
    {
        // Packaged desktop apps (WhatsApp, Claude, …) often have no window-level AUMID, but their
        // process knows its package identity — and that's what Start's icon is keyed by.
        if (string.IsNullOrEmpty(appUserModelId) && processId is { } pid)
            appUserModelId = PackagedAppId(pid);

        var key = $"{appUserModelId}|{exePath}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? icon = null;
        if (!string.IsNullOrEmpty(appUserModelId))
            icon = Load(@"shell:AppsFolder\" + appUserModelId);
        // Executables under WindowsApps are access-protected; the shell answers with a generic
        // document icon there, which is worse than the window's own icon.
        if (icon is null && !string.IsNullOrEmpty(exePath) &&
            !exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            icon = ExtractExeIcon(exePath);

        Cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// The executable's own icon resource at 256 px. Returns null when it has none (the shell would
    /// answer with a generic placeholder; the caller then prefers the window's icon).
    /// </summary>
    private static ImageSource? ExtractExeIcon(string path)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        if (PrivateExtractIcons(path, 0, Size, Size, icons, ids, 1, 0) != 1 || icons[0] == IntPtr.Zero)
            return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icons[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon extraction failed for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static string? PackagedAppId(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            uint length = 256;
            var id = new System.Text.StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder id);

    private static ImageSource? Load(string parsingName)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
                return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap) != 0)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return FromHBitmap(hbitmap);
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon load failed for '{parsingName}': {ex.Message}");
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    /// <summary>Copies a 32-bpp DIB (premultiplied alpha) into a frozen BitmapSource, keeping transparency.</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        var dib = new DIBSECTION();
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
            return null;

        int width = dib.dsBm.bmWidth, height = dib.dsBm.bmHeight, stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        if (dib.dsBmih.biHeight > 0)
        {
            // Bottom-up DIB: flip rows.
            var flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            pixels = flipped;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    // ── Interop ──────────────────────────────────────────────────────────────

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, ref DIBSECTION obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}
