// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CouchSubs.App.Views;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Tests;

/// <summary>
/// Gelişmiş görünüm: bulanık arka plan katmanı ve boyanması, parlaklık ve doku, gradyan zemin, kenarlık, animasyon.
/// Varsayılan görünümde hiçbiri kutuya eklenmez (önceki kutu değişmez).
/// </summary>
public class AdvancedBoxTests
{
    private static DesktopLook Look(Func<DesktopSubtitleStyle, DesktopSubtitleStyle> change, double scale = 1) =>
        DesktopLook.For(DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), change), scale, null);

    private static Border Box(DesktopLook look, string text = "Hold on.") =>
        DesktopSubtitleBox.Make(look, text, text, current: true, paragraphs: false, 1, band: false, speakerColors: false, new SpeakerColorBook());

    [Fact]
    public void DefaultBoxHasNoExtraLayers() => Sta.Run(() =>
    {
        var parts = Box(DesktopLook.For(DesktopStyles.Resolve(new DesktopSubtitleStyle(), new SubtitleStyle()), 1, null)).Parts();
        Assert.Null(parts.Backdrop);
        Assert.Null(parts.Light);
        Assert.Null(parts.Noise);
        Assert.NotNull(parts.Tint);
        Assert.Equal(new Thickness(0), parts.Frame.BorderThickness);
        Assert.Equal(2, parts.Layers.Children.Count);
    });

    [Fact]
    public void BlurAddsBackdropBrightnessAndNoiseInOrder() => Sta.Run(() =>
    {
        var parts = Box(Look(s => s with { BackgroundBlur = true, BlurBrightness = -20, NoiseOpacity = 4, CornerRadius = 12 }, scale: 2)).Parts();
        Assert.NotNull(parts.Backdrop);
        Assert.Equal(DesktopSubtitleBox.BackdropTag, parts.Backdrop!.Tag);
        Assert.Equal(24, parts.Backdrop.RadiusX);
        Assert.Equal(Brushes.Black, parts.Light!.Fill);
        Assert.Equal(0.2, parts.Light.Opacity, 3);
        Assert.Equal(0.04, parts.Noise!.Opacity, 3);
        // Alttan üste: bulanık görüntü, parlaklık, ton, doku, yazı.
        var order = parts.Layers.Children.Cast<UIElement>().ToList();
        Assert.True(order.IndexOf(parts.Backdrop) < order.IndexOf(parts.Light) && order.IndexOf(parts.Light) < order.IndexOf(parts.Tint!)
                    && order.IndexOf(parts.Tint!) < order.IndexOf(parts.Noise) && order.IndexOf(parts.Noise) < order.IndexOf(parts.Frame));
    });

    [Fact]
    public void BrighteningUsesWhite() => Sta.Run(() =>
        Assert.Equal(Brushes.White, Box(Look(s => s with { BackgroundBlur = true, BlurBrightness = 30 })).Parts().Light!.Fill));

    [Fact]
    public void GradientBackgroundFollowsTheAngle() => Sta.Run(() =>
    {
        var look = Look(s => s with { BackgroundGradient = true, BackgroundColor = "#FF0000", GradientColor = "#0000FF", BackgroundOpacity = 50, GradientAngle = 90 });
        var brush = Assert.IsType<LinearGradientBrush>(Box(look).Parts().Tint!.Fill);
        Assert.Equal(Color.FromArgb(128, 255, 0, 0), brush.GradientStops[0].Color);
        Assert.Equal(Color.FromArgb(128, 0, 0, 255), brush.GradientStops[1].Color);
        Assert.Equal(0.5, brush.StartPoint.X, 3);
        Assert.Equal(0, brush.StartPoint.Y, 3);
        Assert.Equal(1, brush.EndPoint.Y, 3);
        Assert.True(brush.IsFrozen);
        // Aynı koyulukta aynı fırça (yeniden kurulmaz).
        Assert.Same(brush, Box(look).Parts().Tint!.Fill);
    });

    [Fact]
    public void BorderIsDrawnWithItsOpacity() => Sta.Run(() =>
    {
        var frame = Box(Look(s => s with { BorderThickness = 2, BorderColor = "#00FF00", BorderOpacity = 50 }, scale: 1.5)).Parts().Frame;
        Assert.Equal(new Thickness(3), frame.BorderThickness);
        Assert.Equal(Color.FromArgb(128, 0, 255, 0), ((SolidColorBrush)frame.BorderBrush).Color);
        Assert.Equal(new Thickness(0), Box(Look(s => s with { BorderThickness = 2, BorderOpacity = 0 })).Parts().Frame.BorderThickness);
    });

    [Fact]
    public void BackdropShowsThePartOfTheImageBehindTheBox() => Sta.Run(() =>
    {
        var look = Look(s => s with { BackgroundBlur = true, BackgroundOpacity = 0, ShadowOpacity = 0, CornerRadius = 0 });
        var box = Box(look);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.VerticalAlignment = VerticalAlignment.Top;
        box.Margin = new Thickness(100, 50, 0, 0);
        var root = new Grid { Width = 400, Height = 200 };
        root.Children.Add(box);
        root.Measure(new Size(400, 200));
        root.Arrange(new Rect(0, 0, 400, 200));
        root.UpdateLayout();
        // Görüntü kökün dörtte biri boyunda (küçültülmüş yakalama gibi): sol yarı kırmızı, sağ yarı mavi.
        var image = Split(100, 50);
        BackdropPainter.Paint(root, image);
        var brush = Assert.IsType<ImageBrush>(box.Parts().Backdrop!.Fill);
        var bounds = box.Parts().Backdrop!.TransformToAncestor(root).TransformBounds(new Rect(box.Parts().Backdrop!.RenderSize));
        Assert.Equal(bounds.X / 4, brush.Viewbox.X, 3);
        Assert.Equal(bounds.Y / 4, brush.Viewbox.Y, 3);
        Assert.Equal(bounds.Width / 4, brush.Viewbox.Width, 3);
        Assert.Equal(BrushMappingMode.Absolute, brush.ViewboxUnits);
        // Yeni kare aynı fırçayı günceller; görüntü kalkınca katman boşalır.
        BackdropPainter.Paint(root, image);
        Assert.Same(brush, box.Parts().Backdrop!.Fill);
        BackdropPainter.Paint(root, null);
        Assert.Null(box.Parts().Backdrop!.Fill);
    });

    [Fact]
    public void NoBackdropMeansNothingToCapture() => Sta.Run(() =>
    {
        var root = new Grid();
        root.Children.Add(Box(Look(s => s with { OutlineThickness = 0 })));
        Assert.False(BackdropPainter.Any(root));
        root.Children.Add(Box(Look(s => s with { BackgroundBlur = true })));
        Assert.True(BackdropPainter.Any(root));
    });

    [Theory]
    [InlineData(DesktopAnimation.Fade, null)]
    [InlineData(DesktopAnimation.Slide, typeof(TranslateTransform))]
    [InlineData(DesktopAnimation.Zoom, typeof(ScaleTransform))]
    public void EnterAnimatesTheInnerLayersOnly(DesktopAnimation animation, Type? transform) => Sta.Run(() =>
    {
        var look = Look(s => s with { Animation = animation, AnimationDuration = 300 });
        var box = Box(look);
        box.Opacity = 0.6;
        BoxAnimation.Enter(box, look.Style, 1);
        var layers = box.Parts().Layers;
        Assert.True(layers.HasAnimatedProperties);
        // Dış kutunun saydamlığı (soluk üst satır) pencereye aittir, animasyon ona dokunmaz.
        Assert.Equal(0.6, box.Opacity);
        Assert.False(box.HasAnimatedProperties);
        if (transform is null) Assert.True(layers.RenderTransform is null || layers.RenderTransform == Transform.Identity);
        else Assert.IsType(transform, layers.RenderTransform);
    });

    [Fact]
    public void NoiseBrushIsSharedAndFrozen() => Sta.Run(() =>
    {
        var first = Box(Look(s => s with { NoiseOpacity = 5 })).Parts().Noise!.Fill;
        Assert.Same(first, Box(Look(s => s with { NoiseOpacity = 10 })).Parts().Noise!.Fill);
        Assert.True(first.IsFrozen);
    });

    private static BitmapSource Split(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x < width / 2 ? 0 : 255);
                pixels[i + 2] = (byte)(x < width / 2 ? 255 : 0);
                pixels[i + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
