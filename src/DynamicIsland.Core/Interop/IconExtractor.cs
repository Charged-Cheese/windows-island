using System.Runtime.InteropServices;

namespace DynamicIsland.Core.Interop;

/// <summary>从 SHELL 提取高质量应用图标（BGRA32 原始像素，避免依赖 System.Drawing）。</summary>
public static class IconExtractor
{
    public sealed record RawIcon(int Width, int Height, byte[] Bgra);

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_ICONONLY = 0x04;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        byte[] lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] bmiColors;
    }

    /// <summary>取 256×256（或指定尺寸）图标，失败返回 null。</summary>
    public static RawIcon? FromPath(string path, int size = 128)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out object item);
            var factory = (IShellItemImageFactory)item;

            factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out hBitmap);
            if (hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var bmp = new BITMAP();
            if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0)
            {
                return null;
            }

            int width = bmp.bmWidth;
            int height = bmp.bmHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 负值 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)pixels.Length
                },
                bmiColors = new uint[3]
            };

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr previous = IntPtr.Zero;
            try
            {
                previous = SelectObject(memDc, hBitmap);
                int lines = GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref info, 0);
                if (lines == 0)
                {
                    return null;
                }
            }
            finally
            {
                SelectObject(memDc, previous);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            // GDI 输出的是 BGRA（小端），WPF 的 Bgra32 正好一致
            return new RawIcon(width, height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }
        }
    }
}
