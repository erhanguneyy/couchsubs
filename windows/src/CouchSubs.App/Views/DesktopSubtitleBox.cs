// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresinin görünümünden (<see cref="DesktopSubtitleStyle"/>) kurulan, dondurulmuş
/// (paylaşılabilir) WPF kaynakları. Görünüm ya da ekran ölçeği değişmedikçe yeniden kurulmaz; her altyazı kutusu
/// bunları paylaşır.
/// </summary>
internal sealed class DesktopLook
{
    public DesktopSubtitleStyle Style { get; }
    public double Scale { get; }
    /// <summary>null: uygulamanın yazı tipi (TextBlock'ta genel stilden gelir).</summary>
    public FontFamily? Family { get; }
    public FontWeight Weight { get; }
    public Brush Text { get; }
    public Color Background { get; }
    /// <summary>Gradyan zeminin ikinci rengi; gradyan yoksa null.</summary>
    public Color? Gradient { get; }
    /// <summary>Kenarlığın fırçası; kenarlık yoksa null.</summary>
    public Brush? Frame { get; }
    private readonly Dictionary<byte, Brush> tints = [];

    /// <summary>Verilen koyulukta zemin (düz ya da gradyan); aynı koyuluk için aynı dondurulmuş fırça.</summary>
    public Brush Tint(double alpha)
    {
        var a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        if (tints.TryGetValue(a, out var cached)) return cached;
        var first = Color.FromArgb(a, Background.R, Background.G, Background.B);
        Brush brush = Gradient is { } second
            ? GradientBrush(first, Color.FromArgb(a, second.R, second.G, second.B), Style.GradientAngle)
            : new SolidColorBrush(first);
        brush.Freeze();
        tints[a] = brush;
        return brush;
    }

    /// <summary>Açıya göre gradyan: 0° soldan sağa, 90° yukarıdan aşağı (kutunun ortasından geçer).</summary>
    private static LinearGradientBrush GradientBrush(Color from, Color to, double angle)
    {
        var radians = angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(radians) / 2, Math.Sin(radians) / 2);
        return new LinearGradientBrush(from, to, new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
    }
    /// <summary>Kontur yoksa null; varsa kalınlığı görünen konturun iki katı (yarısı yazının altında kalır).</summary>
    public Pen? Outline { get; }
    public Effect? Shadow { get; }

    private DesktopLook(DesktopSubtitleStyle style, double scale)
    {
        Style = style;
        Scale = scale;
        var font = DesktopStyles.AvailableFont(style.FontFamily, InstalledFonts.Names);
        Family = font.Length == 0 ? null : new FontFamily(font);
        Weight = FontWeight.FromOpenTypeWeight(style.FontWeight);
        Text = Frozen(new SolidColorBrush(ToColor(style.TextColor)));
        Background = ToColor(style.BackgroundColor);
        Gradient = style.BackgroundGradient ? ToColor(style.GradientColor) : null;
        if (style.BorderThickness > 0 && style.BorderOpacity > 0)
        {
            var border = ToColor(style.BorderColor);
            Frame = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(style.BorderOpacity / 100.0 * 255), border.R, border.G, border.B)));
        }
        if (style.OutlineThickness > 0)
        {
            var pen = new Pen(Frozen(new SolidColorBrush(ToColor(style.OutlineColor))), 2 * style.OutlineThickness * scale)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            Outline = pen;
        }
        if (style.ShadowOpacity > 0)
        {
            // Önceki pencerenin gölgesi: 2 px aşağı, 4 px yumuşak, tam siyah (TV'yi izle görünümünde aynısı).
            var shadow = new DropShadowEffect
            {
                Color = ToColor(style.ShadowColor),
                ShadowDepth = 2 * scale,
                BlurRadius = style.ShadowBlur * scale,
                Opacity = style.ShadowOpacity / 100.0,
                Direction = 270,
            };
            shadow.Freeze();
            Shadow = shadow;
        }
    }

    /// <summary>Önceki görünüm aynıysa onu döndürür (fırça, kalem, gölge yeniden kurulmaz).</summary>
    public static DesktopLook For(DesktopSubtitleStyle style, double scale, DesktopLook? previous) =>
        previous is not null && previous.Style == style && previous.Scale.Equals(scale) ? previous : new DesktopLook(style, scale);

    public static Color ToColor(string hex)
    {
        var (r, g, b) = DesktopStyles.Channels(hex);
        return Color.FromRgb(r, g, b);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// Altyazı kutusu: pencere (<see cref="SubtitleOverlayWindow"/>) ve ayarlardaki canlı önizleme aynı kutuyu çizer.
/// Katmanlar (alttan üste): bulanık arka plan (<see cref="BackdropTag"/>; görüntüyü <see cref="BackdropPainter"/>
/// verir), parlaklık, ton (düz renk ya da gradyan), akrilik doku, en üstte kenarlık ve yazı. Kontur yoksa yazı sıradan
/// TextBlock (TV'yi izle görünümü önceki sürümlerle birebir aynı görünür), varsa <see cref="OutlinedText"/>. Konuşana
/// göre renkler, satır sarma ve soluk üst satırlar korunur. Dış kutunun saydamlığı pencereye (soluk üst satırlar)
/// aittir; beliriş animasyonu iç katmanlara (<see cref="Layers"/>) uygulanır.
/// </summary>
internal static class DesktopSubtitleBox
{
    /// <summary>Bulanık arka plan katmanının işareti.</summary>
    public const string BackdropTag = "desktop-backdrop";

    /// <summary>Kutunun parçaları (testler ve animasyon için).</summary>
    public sealed record Parts(Grid Layers, Rectangle? Backdrop, Rectangle? Light, Rectangle? Tint, Rectangle? Noise, Border Frame, FrameworkElement Text);

    /// <param name="shown">Ekrana yazılacak (sarılmış) metin.</param>
    /// <param name="source">Satırın özgün metni; konuşan adları buradan çıkarılır.</param>
    /// <param name="fontScale">Uzun çeviri 3 satıra sığmayınca yazının küçülme oranı (<see cref="SubtitleWrap"/>).</param>
    /// <param name="book">Konuşanların oturum boyunca aynı kalan renkleri (TV ve yayın penceresiyle aynı defter).</param>
    public static Border Make(DesktopLook look, string shown, string source, bool current, bool paragraphs, double fontScale,
        bool band, bool speakerColors, SpeakerColorBook book)
    {
        var style = look.Style;
        var scale = look.Scale;
        // Satır yüksekliği sıfır olursa WPF hata verir; boy en az 1.
        var size = Math.Max(1, style.FontSize * scale * (current ? 1 : 0.8) * (double.IsFinite(fontScale) && fontScale > 0 ? fontScale : 1));
        var alpha = style.BackgroundOpacity / 100.0 * (current ? 1 : 0.83);
        // "Ekranı çevir" listeleri (başlık ve maddeler) her zaman sola yaslıdır.
        var alignment = paragraphs ? TextAlignment.Left : style.Alignment switch
        {
            DesktopTextAlignment.Left => TextAlignment.Left,
            DesktopTextAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        FrameworkElement text = look.Outline is null
            ? MakeTextBlock(look, shown, source, size, alignment, speakerColors, book)
            : new OutlinedText(look, shown, speakerColors ? SpeakerColors.Speakers(source) : [], book, size, alignment);
        text.Effect = look.Shadow;
        var radius = band ? 0 : style.CornerRadius * scale;
        var layers = new Grid();
        if (style.BackgroundBlur)
        {
            layers.Children.Add(Layer(null, radius, BackdropTag));
            if (style.BlurBrightness != 0)
            {
                var light = Layer(style.BlurBrightness > 0 ? Brushes.White : Brushes.Black, radius);
                light.Opacity = Math.Abs(style.BlurBrightness) / 100.0;
                layers.Children.Add(light);
            }
        }
        if (alpha > 0) layers.Children.Add(Layer(look.Tint(alpha), radius));
        if (style.NoiseOpacity > 0)
        {
            var noise = Layer(Noise.Brush, radius);
            noise.Opacity = style.NoiseOpacity / 100.0;
            layers.Children.Add(noise);
        }
        var padding = current ? new Thickness(26 * scale, 10 * scale, 26 * scale, 10 * scale) : new Thickness(20 * scale, 6 * scale, 20 * scale, 6 * scale);
        layers.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(look.Frame is null ? 0 : style.BorderThickness * scale),
            BorderBrush = look.Frame,
            Padding = padding,
            Child = text,
        });
        return new Border
        {
            // Bant: kutu pencerenin tüm genişliğini kaplar, oyundaki İngilizce yazıyı yan yana da gizler.
            Margin = new Thickness(band ? -24 * scale : 0, 14 * scale, band ? -24 * scale : 0, 0),
            HorizontalAlignment = band ? HorizontalAlignment.Stretch : style.Alignment switch
            {
                DesktopTextAlignment.Left => HorizontalAlignment.Left,
                DesktopTextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            Child = layers,
        };
    }

    /// <summary>Kutunun katmanları; kutu bu sınıfla kurulmadıysa null.</summary>
    public static Parts? Split(Border box)
    {
        if (box.Child is not Grid layers || layers.Children.OfType<Border>().LastOrDefault() is not { Child: FrameworkElement text } frame) return null;
        var shapes = layers.Children.OfType<Rectangle>().ToList();
        var backdrop = shapes.FirstOrDefault(shape => Equals(shape.Tag, BackdropTag));
        var noise = shapes.FirstOrDefault(shape => shape.Fill == Noise.Brush);
        var light = shapes.FirstOrDefault(shape => shape != backdrop && (shape.Fill == Brushes.White || shape.Fill == Brushes.Black));
        var tint = shapes.FirstOrDefault(shape => shape != backdrop && shape != noise && shape != light);
        return new Parts(layers, backdrop, light, tint, noise, frame, text);
    }

    private static Rectangle Layer(Brush? fill, double radius, string? tag = null) => new()
    {
        Fill = fill,
        RadiusX = radius,
        RadiusY = radius,
        Tag = tag,
        IsHitTestVisible = false,
    };

    private static TextBlock MakeTextBlock(DesktopLook look, string shown, string source, double size, TextAlignment alignment, bool speakerColors,
        SpeakerColorBook book)
    {
        var block = new TextBlock
        {
            FontSize = size,
            FontWeight = look.Weight,
            Foreground = look.Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = alignment,
            LineHeight = size * look.Style.LineSpacing,
            // Satır aralığı ayardaki gibi kalsın (uygulamanın genel TextBlock stiliyle aynı).
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (look.Family is { } family) block.FontFamily = family;
        // Konuşana göre renk: "Ad: …" satırı o konuşanın renginde (ayar kapalıysa düz metin).
        SpeakerText.Fill(block, shown, source, speakerColors, book);
        return block;
    }
}

/// <summary>
/// Konturlu altyazı yazısı: yazının çizgisi (geometrisi) önce kalın kalemle, ardından yazının kendisi üstüne çizilir;
/// böylece kontur yalnız harflerin dışında görünür ve konuşana göre renkli parçalar korunur. Biçimlenmiş yazı ve
/// geometrisi yalnız genişlik ya da ekranın DPI'ı değişince yeniden kurulur.
/// </summary>
internal sealed class OutlinedText : FrameworkElement
{
    private readonly DesktopLook look;
    private readonly string text;
    private readonly IReadOnlyList<string> speakers;
    private readonly SpeakerColorBook book;
    private readonly double size;
    private readonly TextAlignment alignment;
    private FormattedText? formatted;
    private Geometry? outline;
    private double layoutWidth = double.NaN;
    private double pixelsPerDip = double.NaN;

    public OutlinedText(DesktopLook look, string text, IReadOnlyList<string> speakers, SpeakerColorBook book, double size, TextAlignment alignment)
    {
        this.look = look;
        this.text = text;
        this.speakers = speakers;
        this.book = book;
        this.size = size;
        this.alignment = alignment;
        // Kontur yazının dışına taşar; kutu kenarında kesilmesin.
        Margin = new Thickness(Pad);
        SnapsToDevicePixels = true;
    }

    /// <summary>Konturun yazının dışına taşan payı.</summary>
    private double Pad => look.Outline is { } pen ? pen.Thickness / 2 : 0;

    internal string Text => text;

    protected override Size MeasureOverride(Size available)
    {
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = double.IsFinite(available.Width) ? Math.Max(1, available.Width) : double.PositiveInfinity;
        var laid = Layout(width, dip);
        return new Size(Math.Min(laid.Width, double.IsFinite(width) ? width : laid.Width), laid.Height);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        // Pencere başka DPI'lı bir ekrana taşındı: yazı yeni piksel yoğunluğuna göre yeniden biçimlenir.
        formatted = null;
        InvalidateMeasure();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (formatted is null || outline is null) return;
        // Satırlar layoutWidth içinde hizalıdır; çizilen alana göre kaydırılır.
        var x = alignment switch
        {
            TextAlignment.Left => 0,
            TextAlignment.Right => RenderSize.Width - formatted.MaxTextWidth,
            _ => (RenderSize.Width - formatted.MaxTextWidth) / 2,
        };
        context.PushTransform(new TranslateTransform(x, 0));
        context.DrawGeometry(null, look.Outline, outline);
        context.DrawText(formatted, new Point(0, 0));
        context.Pop();
    }

    /// <summary>Biçimlenmiş yazıyı (gerekirse) kurar; boyu döner.</summary>
    private Size Layout(double width, double dip)
    {
        if (formatted is null || !dip.Equals(pixelsPerDip) || !(width.Equals(layoutWidth)))
        {
            formatted = Format(dip);
            // Genişlik sınırsızsa en uzun satır kadar; satırlar bu genişliğin içinde hizalanır.
            formatted.MaxTextWidth = double.IsFinite(width) ? width : Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
            var geometry = formatted.BuildGeometry(new Point(0, 0));
            geometry.Freeze();
            outline = geometry;
            layoutWidth = width;
            pixelsPerDip = dip;
        }
        return new Size(Math.Ceiling(formatted.Width), Math.Ceiling(Math.Max(formatted.Height, size * look.Style.LineSpacing)));
    }

    private FormattedText Format(double dip)
    {
        var family = look.Family ?? Application.Current?.TryFindResource("TextFont") as FontFamily ?? new FontFamily("Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, look.Weight, FontStretches.Normal);
        var result = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, look.Text, dip)
        {
            TextAlignment = alignment,
            LineHeight = size * look.Style.LineSpacing,
            Trimming = TextTrimming.None,
        };
        // Konuşana göre renk: "Ad: …" satırları o konuşanın renginde.
        var start = 0;
        foreach (var segment in SpeakerColors.Segments(text, speakers, book.Color))
        {
            if (segment.Color is { } hex && segment.Text.Length > 0)
            {
                var brush = new SolidColorBrush(DesktopLook.ToColor(hex));
                brush.Freeze();
                result.SetForegroundBrush(brush, start, segment.Text.Length);
            }
            start += segment.Text.Length;
        }
        return result;
    }
}

/// <summary>Akrilik doku: döşenen ince gri kumlanma (bir kez üretilir, dondurulmuş).</summary>
internal static class Noise
{
    private const int Size = 128;

    public static Brush Brush { get; } = Create();

    private static ImageBrush Create()
    {
        var pixels = new byte[Size * Size * 4];
        var random = new Random(20261121);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var value = (byte)random.Next(0, 256);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Size, Size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Kutuların bulanık arka plan katmanlarını (<see cref="DesktopSubtitleBox.BackdropTag"/>) bulanık görüntünün
/// kutunun arkasına düşen parçasıyla doldurur. Pencerede görüntü ekrandan yakalanır (<see cref="ScreenBackdrop"/>),
/// ayarlardaki önizlemede örnek sahneden üretilir; ikisi de bu sınıfla boyanır.
/// </summary>
internal static class BackdropPainter
{
    /// <param name="root">Görüntünün kapladığı öğe (pencere içeriği ya da önizleme sahnesi).</param>
    /// <param name="image">Bulanık görüntü; <paramref name="root"/>'un tamamına karşılık gelir.</param>
    public static void Paint(FrameworkElement root, BitmapSource? image)
    {
        if (root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
        foreach (var layer in Backdrops(root))
        {
            if (image is null)
            {
                layer.Fill = null;
                continue;
            }
            // IsVisible değil: önizlemede ve testte pencere dışındaki öğe de boyanabilsin; boyutsuz katman atlanır.
            if (layer.ActualWidth <= 0 || layer.ActualHeight <= 0 || !root.IsAncestorOf(layer)) continue;
            var bounds = layer.TransformToAncestor(root).TransformBounds(new Rect(layer.RenderSize));
            var viewbox = new Rect(bounds.X / root.ActualWidth * image.PixelWidth, bounds.Y / root.ActualHeight * image.PixelHeight,
                bounds.Width / root.ActualWidth * image.PixelWidth, bounds.Height / root.ActualHeight * image.PixelHeight);
            if (layer.Fill is ImageBrush { IsFrozen: false } brush && ReferenceEquals(brush.ImageSource, image))
            {
                if (brush.Viewbox != viewbox) brush.Viewbox = viewbox;
                continue;
            }
            layer.Fill = new ImageBrush(image) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox, Stretch = Stretch.Fill };
        }
    }

    /// <summary>Ağaçtaki bulanık arka plan katmanları.</summary>
    public static IEnumerable<Rectangle> Backdrops(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Rectangle { Tag: DesktopSubtitleBox.BackdropTag } layer) yield return layer;
            else foreach (var nested in Backdrops(child)) yield return nested;
        }
    }

    /// <summary>Ağaçta bulanık arka plan katmanı var mı (yoksa ekran yakalanmaz).</summary>
    public static bool Any(DependencyObject root) => Backdrops(root).Any();
}

/// <summary>
/// Altyazı kutusunun beliriş ve kayboluş animasyonu. Kutunun iç katmanlarına uygulanır: dış kutunun saydamlığı
/// pencereye aittir (soluk üst satırlar, solan önceki satır) ve onunla çakışmaz. Yerleşimi değiştirmez (yalnız çizim
/// dönüşümü), satırlar zıplamaz.
/// </summary>
internal static class BoxAnimation
{
    public static void Enter(Border box, DesktopSubtitleStyle style, double scale) => Run(box, style, scale, entering: true, done: null);

    public static void Exit(Border box, DesktopSubtitleStyle style, Action done) => Run(box, style, 1, entering: false, done);

    private static void Run(Border box, DesktopSubtitleStyle style, double scale, bool entering, Action? done)
    {
        var target = DesktopSubtitleBox.Split(box)?.Layers as UIElement ?? box;
        var duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, style.AnimationDuration)));
        IEasingFunction ease = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn };
        var fade = new DoubleAnimation(entering ? 0 : target.Opacity, entering ? 1 : 0, duration) { EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        target.BeginAnimation(UIElement.OpacityProperty, fade);
        switch (style.Animation)
        {
            case DesktopAnimation.Slide:
            {
                var offset = 18 * scale;
                var move = new TranslateTransform();
                target.RenderTransform = move;
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(entering ? offset : 0, entering ? 0 : offset / 2, duration) { EasingFunction = ease });
                break;
            }
            case DesktopAnimation.Zoom:
            {
                var zoom = new ScaleTransform();
                target.RenderTransformOrigin = new Point(0.5, 0.5);
                target.RenderTransform = zoom;
                var animation = new DoubleAnimation(entering ? 0.88 : 1, entering ? 1 : 0.94, duration) { EasingFunction = ease };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
                break;
            }
        }
    }
}
