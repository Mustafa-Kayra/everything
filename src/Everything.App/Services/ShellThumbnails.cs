using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Everything.App.Native;

namespace Everything.App.Services;

/// <summary>
/// Gezgin'in küçük resimleri (resim, video, PDF, Office… — yüklü küçük resim işleyicisi olan
/// her tür). Kabuk nesneleri STA gerektirdiğinden istekler tek bir STA iş parçacığında işlenir.
/// </summary>
internal static class ShellThumbnails
{
    private static readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<BitmapSource?> Result)> Queue = new();

    static ShellThumbnails()
    {
        var thread = new Thread(Worker) { IsBackground = true, Name = "ShellThumbnails" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<BitmapSource?> GetAsync(string path, int size)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((path, size, tcs));
        return tcs.Task;
    }

    private static void Worker()
    {
        foreach (var (path, size, result) in Queue.GetConsumingEnumerable())
        {
            // Kullanıcı hızlı gezinirken eskiyen istekleri atla; yalnızca en sonuncuyu işle.
            if (Queue.Count > 0)
            {
                result.TrySetResult(null);
                continue;
            }
            try
            {
                result.TrySetResult(Extract(path, size));
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Küçük resim alınamadı ({path}): {ex.Message}");
                result.TrySetResult(null);
            }
        }
    }

    private static BitmapSource? Extract(string path, int size)
    {
        Win32.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(Win32.IShellItemImageFactory).GUID, out var factory);
        try
        {
            var hr = factory.GetImage(new Win32.NativeSize { Width = size, Height = size }, Win32.ImageFactoryFlags.ResizeToFit | Win32.ImageFactoryFlags.BiggerSizeOk, out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                return ToBitmapSource(hbitmap);
            }
            finally
            {
                Win32.DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>32 bit HBITMAP'i saydamlığı koruyarak kopyalar (CreateBitmapSourceFromHBitmap alfayı kaybeder).</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<Win32.BitmapInfo>(), out var info) == 0 || info.Bits == IntPtr.Zero || info.BitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        var stride = info.WidthBytes;
        var height = Math.Abs(info.Height);
        var pixels = new byte[stride * height];
        Marshal.Copy(info.Bits, pixels, 0, pixels.Length);

        // DIB'ler genelde alttan üste saklanır: satırları çevir.
        if (info.Height > 0)
        {
            var row = new byte[stride];
            for (var y = 0; y < height / 2; y++)
            {
                Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, (height - 1 - y) * stride, pixels, y * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, (height - 1 - y) * stride, stride);
            }
        }

        // Alfa kanalı tamamen boşsa (alfasız görüntü) opak kabul et; tamamen boş görüntüyü yok say.
        var hasAlpha = false;
        var hasColor = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            hasAlpha |= pixels[i + 3] != 0;
            hasColor |= (pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0;
            if (hasAlpha && hasColor)
                break;
        }
        if (!hasColor)
            return null;
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var bitmap = BitmapSource.Create(info.Width, height, 96, 96, format, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
