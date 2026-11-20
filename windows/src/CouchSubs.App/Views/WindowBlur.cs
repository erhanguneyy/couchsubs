// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Altyazı penceresinin arkasını bulanıklaştırır (Windows'un pencere bulanıklığı, DWM). Bulanıklık bütün pencereye
/// uygulanır; bu yüzden açıkken pencere görünen kutuların şekline kırpılır. API belgelenmemiştir: desteklenmeyen
/// Windows'ta çağrı sessizce başarısız olur, altyazı bulanıklıksız görünür.
/// </summary>
internal static partial class WindowBlur
{
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentBlurBehind = 3;
    private const int RgnOr = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    /// <summary>Bulanıklığı açar ya da kapatır; renk katmanı yok (kutunun kendi zemini üstüne çizilir).</summary>
    public static unsafe bool SetEnabled(IntPtr window, bool enabled)
    {
        if (window == IntPtr.Zero) return false;
        var accent = new AccentPolicy { AccentState = enabled ? AccentBlurBehind : AccentDisabled };
        var data = new CompositionData { Attribute = WcaAccentPolicy, Data = (IntPtr)(&accent), SizeOfData = sizeof(AccentPolicy) };
        try
        {
            return SetWindowCompositionAttribute(window, ref data);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pencereyi verilen kutuların birleşimine kırpar; null ise kırpma kalkar (bütün pencere). Boş liste: pencerenin
    /// hiçbir yeri çizilmez (görünen kutu yok).
    /// </summary>
    public static void SetRegion(IntPtr window, IReadOnlyList<PixelBox>? boxes)
    {
        if (window == IntPtr.Zero) return;
        if (boxes is null)
        {
            SetWindowRgn(window, IntPtr.Zero, true);
            return;
        }
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return;
        foreach (var box in boxes)
        {
            var part = box.CornerDiameter > 0
                ? CreateRoundRectRgn(box.Left, box.Top, box.Right + 1, box.Bottom + 1, box.CornerDiameter, box.CornerDiameter)
                : CreateRectRgn(box.Left, box.Top, box.Right, box.Bottom);
            if (part == IntPtr.Zero) continue;
            CombineRgn(region, region, part, RgnOr);
            DeleteObject(part);
        }
        // Başarılıysa bölge artık pencerenindir, silinmez.
        if (SetWindowRgn(window, region, true) == 0) DeleteObject(region);
    }
}
