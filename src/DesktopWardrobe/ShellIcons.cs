using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopWardrobe;

/// <summary>Gets the same big icon (or thumbnail) Explorer shows for a file or folder.</summary>
internal static class ShellIcons
{
    private const int SIIGBF_ICONONLY = 0x04;
    private const int SIIGBF_INCACHEONLY = 0x10;

    private static readonly BlockingCollection<Action> Queue = StartWorker();

    /// <summary>
    /// Loads the icon on a background thread, so slow shell extensions never freeze the line, and
    /// hands it to <paramref name="done"/> on the calling (UI) thread.
    /// </summary>
    public static void LoadAsync(string path, int size, Action<ImageSource?, ImageSource?> done)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Queue.Add(() =>
        {
            // A thumbnail Explorer already has, else the plain type icon. Never generate a new thumbnail:
            // for OneDrive files that would download the file.
            var image = Get(path, size, SIIGBF_INCACHEONLY) ?? Get(path, size, SIIGBF_ICONONLY);
            var shadow = image != null ? CreateShadow(image) : null;
            dispatcher.BeginInvoke(() => done(image, shadow));
        });
    }

    private static BlockingCollection<Action> StartWorker()
    {
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "Shell icons",
        };
        thread.SetApartmentState(ApartmentState.STA); // shell objects expect an STA
        thread.Start();
        return queue;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

    /// <summary>
    /// A soft black silhouette of the icon, made once here so drawing it later costs no more than
    /// drawing any other picture (a live blur effect or opacity mask is redone on every frame).
    /// </summary>
    private static BitmapSource CreateShadow(BitmapSource icon)
    {
        int width = icon.PixelWidth, height = icon.PixelHeight;
        var pixels = new byte[width * height * 4];
        icon.CopyPixels(pixels, width * 4, 0);

        var alpha = new float[width * height];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3] * 0.35f;

        for (int pass = 0; pass < 2; pass++)
            alpha = BoxBlur(alpha, width, height, radius: 2);

        var shadow = new byte[pixels.Length];
        for (int i = 0; i < alpha.Length; i++)
            shadow[i * 4 + 3] = (byte)Math.Clamp(alpha[i], 0, 255); // premultiplied black: colour stays 0

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, shadow, width * 4);
        image.Freeze();
        return image;
    }

    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[source.Length];
        var result = new float[source.Length];
        int span = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++)
                    sum += source[y * width + Math.Clamp(x + k, 0, width - 1)];
                horizontal[y * width + x] = sum / span;
            }
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++)
                    sum += horizontal[Math.Clamp(y + k, 0, height - 1) * width + x];
                result[y * width + x] = sum / span;
            }
        }
        return result;
    }

    private static BitmapSource? Get(string path, int size, int flags)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbitmap) != 0)
                return null;
            return FromHBitmap(hbitmap);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                Native.DeleteObject(hbitmap);
        }
    }

    // CreateBitmapSourceFromHBitmap drops the alpha channel, so copy the 32-bit pixels ourselves.
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (Native.GetObject(hbitmap, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0)
            return null;

        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };

        var pixels = new byte[width * height * 4];
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }

        // Some icons come without any alpha at all; treat those as fully opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length && !hasAlpha; i += 4)
            hasAlpha = pixels[i] != 0;
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
