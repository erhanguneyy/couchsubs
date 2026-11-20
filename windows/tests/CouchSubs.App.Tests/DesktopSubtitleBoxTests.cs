// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using CouchSubs.App.Views;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Tests;

/// <summary>
/// Bilgisayar ekranındaki altyazı kutusunun çizimi: TV'yi izleyen görünüm önceki pencerenin birebir aynısıdır;
/// konturlu yazı çok satırda doğru ölçülür ve konuşan renklerini korur; WPF kaynakları gereksiz yere yeniden kurulmaz.
/// </summary>
public class DesktopSubtitleBoxTests
{
    private const string ThreeLines = "We need to move,\nthe law is right\nbehind us.";

    private static DesktopLook Look(DesktopSubtitleStyle style, double scale = 1) =>
        DesktopLook.For(DesktopStyles.Resolve(style, new SubtitleStyle()), scale, null);

    private static DesktopSubtitleStyle Custom(Func<DesktopSubtitleStyle, DesktopSubtitleStyle> change) =>
        DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), change);

    private static Border Box(DesktopLook look, string text = ThreeLines, bool current = true, bool paragraphs = false, bool band = false,
        bool speakers = true) =>
        DesktopSubtitleBox.Make(look, text, text, current, paragraphs, 1, band, speakers, new SpeakerColorBook());

    private static Size Measure(UIElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        return element.DesiredSize;
    }

    [Fact]
    public void MatchTvDrawsThePreviousBox() => Sta.Run(() =>
    {
        var box = Box(Look(new DesktopSubtitleStyle(), scale: 2));
        var text = Assert.IsType<TextBlock>(box.Text());
        Assert.Equal(46 * 2, text.FontSize);
        Assert.Equal(FontWeights.SemiBold, text.FontWeight);
        Assert.Equal(Colors.White, ((SolidColorBrush)text.Foreground).Color);
        Assert.Equal(TextAlignment.Center, text.TextAlignment);
        Assert.Equal(46 * 2 * 1.3, text.LineHeight, 3);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        var shadow = Assert.IsType<DropShadowEffect>(text.Effect);
        Assert.Equal((4.0 * 2, 2.0 * 2, 1.0, 270.0), (shadow.BlurRadius, shadow.ShadowDepth, shadow.Opacity, shadow.Direction));
        Assert.Equal(Color.FromArgb(230, 0, 0, 0), ((SolidColorBrush)box.Tint()!.Fill).Color);
        Assert.Equal(new CornerRadius(20), box.Frame().CornerRadius);
        Assert.Equal(HorizontalAlignment.Center, box.HorizontalAlignment);
        // Uygulamanın yazı tipi genel stilden gelir; kutu onu ezmez.
        Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(text, TextBlock.FontFamilyProperty).BaseValueSource);
    });

    [Fact]
    public void PreviousLineIsSmallerAndLighter() => Sta.Run(() =>
    {
        var box = Box(Look(new DesktopSubtitleStyle()), current: false);
        Assert.Equal(46 * 0.8, ((TextBlock)box.Text()).FontSize, 3);
        Assert.Equal((byte)Math.Round(0.9 * 0.83 * 255), ((SolidColorBrush)box.Tint()!.Fill).Color.A);
    });

    [Fact]
    public void MultilineTextKeepsItsLines() => Sta.Run(() =>
    {
        var box = Box(Look(new DesktopSubtitleStyle()));
        var size = Measure(box, 1600);
        var text = (TextBlock)box.Text();
        Assert.Equal(ThreeLines, text.Text);
        // Üç satır, her biri satır yüksekliğinde (genişlik yeterli; ek sarma yok).
        Assert.InRange(text.ActualHeight, 3 * text.LineHeight - 1, 3 * text.LineHeight + 1);
        Assert.True(size.Width < 1600);
    });

    [Fact]
    public void OutlinedTextMeasuresEveryLine() => Sta.Run(() =>
    {
        var look = Look(Custom(s => s with { OutlineThickness = 2, LineSpacing = 1.5 }));
        var box = Box(look);
        var outlined = Assert.IsType<OutlinedText>(box.Text());
        Measure(box, 1600);
        var lineHeight = 46 * 1.5;
        Assert.InRange(outlined.ActualHeight, 3 * lineHeight - 1, 3 * lineHeight + 1);
        // Kontur yazının dışına taşar; kutuda payı vardır.
        Assert.Equal(new Thickness(2), outlined.Margin);
    });

    [Fact]
    public void OutlinedTextWrapsWhenNarrow() => Sta.Run(() =>
    {
        var look = Look(Custom(s => s with { OutlineThickness = 2 }));
        const string text = "This is a long translated subtitle line that has to wrap on a narrow window";
        var wide = Measure(Box(look, text), 4000);
        var narrow = Measure(Box(look, text), 400);
        Assert.True(narrow.Height > wide.Height * 2, $"{narrow.Height} / {wide.Height}");
        Assert.True(narrow.Width <= 400);
    });

    [Fact]
    public void OutlineIsDrawnAroundTheTextAndSpeakerColorsStay() => Sta.Run(() =>
    {
        var look = Look(Custom(s => s with
        {
            OutlineThickness = 3, OutlineColor = "#FF0000", TextColor = "#FFFFFF", ShadowOpacity = 0, BackgroundOpacity = 0,
        }));
        const string text = "Mattia: Hold on.";
        var book = new SpeakerColorBook();
        var box = DesktopSubtitleBox.Make(look, text, text, current: true, paragraphs: false, 1, band: false, speakerColors: true, book);
        Assert.Null(box.Text().Effect);
        var pixels = Render(box, 900);
        Assert.Contains(pixels, p => p.R > 200 && p.G < 60 && p.B < 60 && p.A > 200);
        // Konuşanın rengi (palet) yazının içinde görünür; düz beyaz değildir.
        // Renk defterinin bu konuşana verdiği renk (oturum boyunca aynı).
        var speaker = DesktopLook.ToColor(book.Color("Mattia")!);
        Assert.Contains(pixels, p => Near(p, speaker));
    });

    [Fact]
    public void ParagraphsStayLeftAlignedAndBandStretches() => Sta.Run(() =>
    {
        var look = Look(Custom(s => s with { Alignment = DesktopTextAlignment.Right }));
        Assert.Equal(TextAlignment.Left, ((TextBlock)Box(look, paragraphs: true).Text()).TextAlignment);
        Assert.Equal(TextAlignment.Right, ((TextBlock)Box(look).Text()).TextAlignment);
        Assert.Equal(HorizontalAlignment.Right, Box(look).HorizontalAlignment);
        var band = Box(look, band: true);
        Assert.Equal(HorizontalAlignment.Stretch, band.HorizontalAlignment);
        Assert.Equal(new CornerRadius(0), band.Frame().CornerRadius);
    });

    [Fact]
    public void NoShadowMeansNoEffect() => Sta.Run(() =>
        Assert.Null(Box(Look(Custom(s => s with { ShadowOpacity = 0 }))).Text().Effect));

    [Fact]
    public void LookIsReusedUntilTheStyleOrScaleChanges() => Sta.Run(() =>
    {
        var style = DesktopStyles.Preset(DesktopStylePreset.Cinematic, 46);
        var first = DesktopLook.For(style, 1, null);
        Assert.Same(first, DesktopLook.For(style with { }, 1, first));
        Assert.NotSame(first, DesktopLook.For(style, 1.5, first));
        // Değişen görünüm (çeviri sürerken ayarlardan) yeni kaynaklarla çizilir.
        var changed = DesktopLook.For(style with { TextColor = "#00FF00" }, 1, first);
        Assert.NotSame(first, changed);
        Assert.Equal(Color.FromRgb(0, 0xFF, 0), ((SolidColorBrush)changed.Text).Color);
        Assert.True(first.Text.IsFrozen && first.Outline!.IsFrozen && first.Shadow!.IsFrozen);
    });

    [Fact]
    public void SharedResourcesAreUsedByEveryBox() => Sta.Run(() =>
    {
        var look = Look(Custom(s => s with { OutlineThickness = 1 }));
        Assert.Same(Box(look).Text().Effect, Box(look, "Another line").Text().Effect);
    });

    [Fact]
    public void MissingFontUsesTheAppFont() => Sta.Run(() =>
    {
        Assert.Null(Look(Custom(s => s with { FontFamily = "Font That Is Not Installed 123" })).Family);
        var installed = InstalledFonts.Names.FirstOrDefault();
        if (installed is null) return;
        Assert.Equal(installed, Look(Custom(s => s with { FontFamily = installed })).Family?.Source);
    });

    private static bool Near(Color pixel, Color wanted) =>
        pixel.A > 200 && Math.Abs(pixel.R - wanted.R) < 12 && Math.Abs(pixel.G - wanted.G) < 12 && Math.Abs(pixel.B - wanted.B) < 12;

    private static List<Color> Render(FrameworkElement element, double width)
    {
        var size = Measure(element, width);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var stride = bitmap.PixelWidth * 4;
        var bytes = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(bytes, stride, 0);
        var result = new List<Color>(bytes.Length / 4);
        for (var i = 0; i < bytes.Length; i += 4) result.Add(Color.FromArgb(bytes[i + 3], bytes[i + 2], bytes[i + 1], bytes[i]));
        return result;
    }
}

/// <summary>Katmanlı kutunun parçalarına kısa erişim.</summary>
internal static class BoxParts
{
    public static DesktopSubtitleBox.Parts Parts(this Border box) => DesktopSubtitleBox.Split(box) ?? throw new InvalidOperationException("katmanlı kutu değil");
    public static FrameworkElement Text(this Border box) => box.Parts().Text;
    public static System.Windows.Shapes.Rectangle? Tint(this Border box) => box.Parts().Tint;
    public static Border Frame(this Border box) => box.Parts().Frame;
}

/// <summary>WPF nesneleri STA iş parçacığında kurulmalı.</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
