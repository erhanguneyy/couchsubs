// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Altyazı penceresinin arkasındaki ekran görüntüsünü (oyun) yakalayıp bulanıklaştırır: saniyede
/// <see cref="FramesPerSecond"/> kez, ayrı bir iş parçacığında. Pencere ekran yakalamasından hariç tutulur
/// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+); yoksa kendi altyazısını yakalayıp bulanıklaştırırdı. Bu yüzden açıkken
/// altyazı penceresi ekran görüntülerinde ve OBS'nin ekran yakalamasında görünmez; kapanınca eski hâline döner.
/// Görüntü küçültülerek yakalanır ve <see cref="BackdropBlur"/> ile bulanıklaştırılır; arayüz iş parçacığına yalnız
/// küçük bir resim gider. Hariç tutma desteklenmiyorsa <see cref="TryStart"/> null döner (Windows'un sabit
/// bulanıklığına düşülür).
/// </summary>
internal sealed partial class ScreenBackdrop : IDisposable
{
    public const int FramesPerSecond = 20;
    private const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;

    private readonly IntPtr window;
    private readonly Dispatcher dispatcher;
    private readonly Action<BitmapSource> delivered;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool disposed;
    private volatile bool running;
    private double radius;
    private int pending;
    private WriteableBitmap? bitmap;

    /// <summary>Son bulanık görüntü (arayüz iş parçacığında); henüz yoksa null.</summary>
    public BitmapSource? Current => bitmap;

    /// <summary>Bulanıklık, ekran pikseli cinsinden.</summary>
    public double RadiusPixels
    {
        get => Volatile.Read(ref radius);
        set => Volatile.Write(ref radius, double.IsFinite(value) ? Math.Max(0, value) : 0);
    }

    /// <summary>Pencere görünmüyorken yakalama durur (işlemci ve ekran kartı boşa çalışmaz).</summary>
    public bool Running
    {
        get => running;
        set
        {
            if (running == value) return;
            running = value;
            if (value) wake.Set();
        }
    }

    private ScreenBackdrop(IntPtr window, Dispatcher dispatcher, Action<BitmapSource> delivered)
    {
        this.window = window;
        this.dispatcher = dispatcher;
        this.delivered = delivered;
        thread = new Thread(Loop) { IsBackground = true, Name = "CouchSubs backdrop", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Pencere yakalamadan hariç tutulabildiyse başlar; yoksa null.</summary>
    public static ScreenBackdrop? TryStart(IntPtr window, Dispatcher dispatcher, Action<BitmapSource> delivered)
    {
        if (window == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;
        try
        {
            if (!SetWindowDisplayAffinity(window, WdaExcludeFromCapture)) return null;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        var backdrop = new ScreenBackdrop(window, dispatcher, delivered);
        backdrop.thread.Start();
        return backdrop;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        running = false;
        wake.Set();
        // Pencere yeniden ekran görüntülerinde ve OBS'de görünür.
        try { SetWindowDisplayAffinity(window, WdaNone); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { /* hiç açılamamıştı */ }
    }

    private void Loop()
    {
        var screen = IntPtr.Zero;
        var memory = IntPtr.Zero;
        var section = IntPtr.Zero;
        var previous = IntPtr.Zero;
        var bits = IntPtr.Zero;
        var (width, height) = (0, 0);
        byte[] buffer = [];
        try
        {
            screen = GetDC(IntPtr.Zero);
            memory = CreateCompatibleDC(screen);
            if (screen == IntPtr.Zero || memory == IntPtr.Zero) return;
            SetStretchBltMode(memory, Halftone);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            var interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
            while (!disposed)
            {
                if (!running)
                {
                    wake.WaitOne();
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                if (GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                {
                    var (downscale, blur) = BackdropBlur.Plan(RadiusPixels);
                    var (sourceWidth, sourceHeight) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
                    var small = (Width: Math.Max(1, (sourceWidth + downscale - 1) / downscale), Height: Math.Max(1, (sourceHeight + downscale - 1) / downscale));
                    if (small.Width != width || small.Height != height)
                    {
                        if (section != IntPtr.Zero)
                        {
                            SelectObject(memory, previous);
                            DeleteObject(section);
                        }
                        var header = new BitmapInfoHeader
                        {
                            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                            Width = small.Width,
                            Height = -small.Height, // yukarıdan aşağı satırlar
                            Planes = 1,
                            BitCount = 32,
                        };
                        section = CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
                        if (section == IntPtr.Zero) return;
                        previous = SelectObject(memory, section);
                        (width, height) = small;
                        buffer = new byte[width * height * 4];
                    }
                    if (StretchBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, sourceWidth, sourceHeight, SrcCopy))
                    {
                        GdiFlush();
                        Marshal.Copy(bits, buffer, 0, buffer.Length);
                        BackdropBlur.Apply(buffer, width, height, blur);
                        // Arayüz önceki kareyi işlemeden yenisi gönderilmez (kareler birikmez).
                        if (Interlocked.Exchange(ref pending, 1) == 0)
                        {
                            var frame = buffer.ToArray();
                            var (frameWidth, frameHeight) = (width, height);
                            dispatcher.BeginInvoke(DispatcherPriority.Render, () => Deliver(frame, frameWidth, frameHeight));
                        }
                    }
                }
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero) wake.WaitOne(remaining);
            }
        }
        catch (Exception error)
        {
            // Yakalama bozulursa altyazı bulanıklıksız sürer; pencere kapanmaz.
            StartupGuard.Log("Arka plan bulanıklığı", error);
        }
        finally
        {
            if (section != IntPtr.Zero)
            {
                SelectObject(memory, previous);
                DeleteObject(section);
            }
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            // wake kapatılmaz: Dispose ve Running onu iş parçacığı bittikten sonra da çağırabilir.
        }
    }

    private void Deliver(byte[] frame, int width, int height)
    {
        Interlocked.Exchange(ref pending, 0);
        if (disposed) return;
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), frame, width * 4, 0);
        delivered(bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect32 rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(IntPtr dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY,
        int sourceWidth, int sourceHeight, uint operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();
}
