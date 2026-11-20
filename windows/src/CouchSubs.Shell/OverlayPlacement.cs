// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Shell;

/// <summary>Bir ekran alanı (WPF birimiyle).</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    /// <summary>Sonlu ve boş olmayan alan (ekran bilgisi okunamayınca sıfır ya da NaN gelebilir).</summary>
    public bool IsUsable => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
                            && Width > 0 && Height > 0;
}

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresinin yeri ve boyu. Kayıtlı yer bozuksa (NaN, sıfır genişlik) ya da artık
/// bağlı olmayan bir ekrandaysa pencere ana ekranın altında ortada açılır; WPF'e hiçbir zaman geçersiz değer verilmez.
/// </summary>
public static class OverlayPlacement
{
    public const double SmallestWidth = 320;
    /// <summary>Kayıtlı yerin ekranda en az bu kadarı görünmüyorsa pencere varsayılan yere döner.</summary>
    public const double VisiblePart = 80;
    /// <summary>Ekran bilgisi hiç okunamazsa kullanılan alan.</summary>
    private static readonly ScreenArea Fallback = new(0, 0, 1280, 720);

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir.</summary>
    public static double Scale(double primaryScreenHeight) =>
        double.IsFinite(primaryScreenHeight) && primaryScreenHeight > 0 ? Math.Clamp(primaryScreenHeight / 1080, 0.5, 4) : 1;

    /// <summary>Ayarlardaki yazı boyu; bozuk kayıtta varsayılan, uç değerlerde sınırda.</summary>
    public static double FontSize(int fontSize) => fontSize > 0 ? Math.Clamp(fontSize, 12, 200) : 46;

    /// <summary>Pencere en çok üç satıra (ve satırların kaymasına) yetecek kadar yüksektir.</summary>
    public static double Height(int fontSize, double scale) => Math.Round(FontSize(fontSize) * scale * 8.1 + 140 * scale);

    /// <summary>Kayıtlı yer (ekranlardan birinde yeterince görünüyorsa) ya da çalışma alanının altında ortada, %70 genişlikte.</summary>
    public static OverlayBounds Place(OverlayBounds? saved, ScreenArea virtualScreen, ScreenArea workArea, double height)
    {
        var work = workArea.IsUsable ? workArea : virtualScreen.IsUsable ? virtualScreen : Fallback;
        var screen = virtualScreen.IsUsable ? virtualScreen : work;
        if (!double.IsFinite(height) || height <= 0) height = Height(0, 1);
        if (saved is { } s && double.IsFinite(s.Left) && double.IsFinite(s.Top) && double.IsFinite(s.Width) && s.Width > 0)
        {
            var width = Math.Clamp(s.Width, SmallestWidth, Math.Max(SmallestWidth, screen.Width));
            if (Visible(s.Left, s.Top, width, height, screen)) return new OverlayBounds(s.Left, s.Top, width);
        }
        var placed = Math.Max(SmallestWidth, Math.Round(work.Width * 0.7));
        return new OverlayBounds(work.Left + (work.Width - placed) / 2, work.Bottom - work.Height * 0.04 - height, placed);
    }

    /// <summary>Kenardan genişletilen pencerenin yeni genişliği: en az <see cref="SmallestWidth"/>, en çok tüm ekranlar.</summary>
    public static double ClampWidth(double width, double virtualScreenWidth)
    {
        // Ekranların genişliği okunamadıysa üst sınır yok.
        var widest = double.IsFinite(virtualScreenWidth) ? Math.Max(SmallestWidth, virtualScreenWidth) : double.MaxValue;
        return double.IsFinite(width) ? Math.Clamp(width, SmallestWidth, widest) : SmallestWidth;
    }

    /// <summary>Kaydedilecek yer; değerlerden biri sonlu değilse (JSON'a yazılamaz) kaydedilmez.</summary>
    public static OverlayBounds? ToSave(double left, double top, double width) =>
        double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && width > 0
            ? new OverlayBounds(Math.Round(left), Math.Round(top), Math.Round(width))
            : null;

    private static bool Visible(double left, double top, double width, double height, ScreenArea screen)
    {
        var across = Math.Min(left + width, screen.Right) - Math.Max(left, screen.Left);
        var down = Math.Min(top + height, screen.Bottom) - Math.Max(top, screen.Top);
        return across >= Math.Min(VisiblePart, width) && down >= Math.Min(VisiblePart, height);
    }
}

/// <summary>
/// Altyazı penceresinin Win32 genişletilmiş biçemleri (GWL_EXSTYLE). Pencere odağı almaz, Alt+Tab'da görünmez;
/// "Tıklamalar geçsin" açıksa fare alttaki pencereye (oyun, PS Remote Play) gider.
/// </summary>
public static class OverlayWindowStyle
{
    public const long Transparent = 0x20;
    public const long ToolWindow = 0x80;
    public const long NoActivate = 0x08000000;

    /// <summary>Pencere açılınca: odağı almaz ve Alt+Tab listesinde görünmez (diğer biçemler korunur).</summary>
    public static long Base(long style) => style | NoActivate | ToolWindow;

    /// <summary>Tıklamalar yalnız ayar açıksa ve pencerenin yeri ayarlanmıyorsa geçer (ayarlanırken sürüklenebilsin).</summary>
    public static long ClickThrough(long style, bool clickThrough, bool adjusting) =>
        clickThrough && !adjusting ? style | Transparent : style & ~Transparent;
}

/// <summary>Pencereye göre piksel cinsinden yuvarlak köşeli dikdörtgen (Win32 bölgesi için; köşe çapı piksel).</summary>
public readonly record struct PixelBox(int Left, int Top, int Right, int Bottom, int CornerDiameter);

/// <summary>
/// Arka plan bulanıklığında pencere yalnız görünen kutuların şekline kırpılır (yoksa bütün pencere dikdörtgeni
/// bulanıklaşırdı). Kutuların WPF birimiyle yerleri ekranın DPI ölçeğiyle piksele çevrilir.
/// </summary>
public static class OverlayBlurRegion
{
    /// <param name="boxes">Kutular: pencereye göre yer (WPF birimi) ve köşe yarıçapı.</param>
    /// <param name="dpiX">Yatay ölçek (96 DPI'da 1, %150'de 1,5).</param>
    public static IReadOnlyList<PixelBox> ToPixels(IEnumerable<(ScreenArea Area, double Radius)> boxes, double dpiX, double dpiY)
    {
        if (!double.IsFinite(dpiX) || dpiX <= 0) dpiX = 1;
        if (!double.IsFinite(dpiY) || dpiY <= 0) dpiY = 1;
        var result = new List<PixelBox>();
        foreach (var (area, radius) in boxes)
        {
            if (!area.IsUsable) continue;
            var left = (int)Math.Floor(area.Left * dpiX);
            var top = (int)Math.Floor(area.Top * dpiY);
            var right = (int)Math.Ceiling(area.Right * dpiX);
            var bottom = (int)Math.Ceiling(area.Bottom * dpiY);
            if (right <= left || bottom <= top) continue;
            var diameter = double.IsFinite(radius) && radius > 0
                ? (int)Math.Round(Math.Min(2 * radius * Math.Min(dpiX, dpiY), Math.Min(right - left, bottom - top)))
                : 0;
            result.Add(new PixelBox(left, top, right, bottom, diameter));
        }
        return result;
    }
}
