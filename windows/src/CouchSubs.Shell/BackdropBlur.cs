// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Shell;

/// <summary>
/// Altyazının arkasındaki görüntünün bulanıklaştırılması. Görüntü önce küçültülür (küçültme de bulanıklaştırır, iş
/// azalır), sonra küçük görüntüye üç geçişli kutu bulanıklığı uygulanır (Gauss bulanıklığına çok yakın). Böylece
/// 40 piksellik bulanıklık bile yalnız birkaç yüz piksel genişliğinde bir görüntüde, işlemcide milisaniyenin altında
/// hesaplanır; WPF onu yeniden büyütürken yumuşatır.
/// </summary>
public static class BackdropBlur
{
    /// <summary>Küçük görüntüdeki bulanıklık yarıçapı bu civarda tutulur.</summary>
    private const double SmallRadius = 3;
    public const int MaxDownscale = 8;

    /// <summary>Piksel cinsinden bulanıklık için küçültme oranı ve küçük görüntüdeki kutu yarıçapı.</summary>
    public static (int Downscale, int Radius) Plan(double radiusPixels)
    {
        if (!double.IsFinite(radiusPixels) || radiusPixels <= 0) return (1, 0);
        var downscale = Math.Clamp((int)Math.Round(radiusPixels / SmallRadius), 1, MaxDownscale);
        var radius = Math.Max(1, (int)Math.Round(radiusPixels / downscale));
        return (downscale, radius);
    }

    /// <summary>
    /// BGRA (32 bit) görüntüye yerinde üç geçişli kutu bulanıklığı; kenarlarda en yakın piksel tekrarlanır (kenar
    /// kararmaz). Saydamlık kanalı tam opak yapılır (ekran görüntüsü).
    /// </summary>
    public static void Apply(byte[] pixels, int width, int height, int radius, int passes = 3)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) return;
        for (var i = 3; i < width * height * 4; i += 4) pixels[i] = 255;
        if (radius <= 0) return;
        var scratch = new byte[width * height * 4];
        for (var pass = 0; pass < passes; pass++)
        {
            Horizontal(pixels, scratch, width, height, radius);
            Vertical(scratch, pixels, width, height, radius);
        }
    }

    private static void Horizontal(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = 2 * radius + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += source[row + Math.Clamp(k, 0, width - 1) * 4 + channel];
                for (var x = 0; x < width; x++)
                {
                    target[row + x * 4 + channel] = (byte)(sum / window);
                    var leaving = Math.Clamp(x - radius, 0, width - 1);
                    var entering = Math.Clamp(x + radius + 1, 0, width - 1);
                    sum += source[row + entering * 4 + channel] - source[row + leaving * 4 + channel];
                }
            }
            for (var x = 0; x < width; x++) target[row + x * 4 + 3] = 255;
        }
    }

    private static void Vertical(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = 2 * radius + 1;
        var stride = width * 4;
        for (var x = 0; x < width; x++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var column = x * 4 + channel;
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += source[Math.Clamp(k, 0, height - 1) * stride + column];
                for (var y = 0; y < height; y++)
                {
                    target[y * stride + column] = (byte)(sum / window);
                    var leaving = Math.Clamp(y - radius, 0, height - 1);
                    var entering = Math.Clamp(y + radius + 1, 0, height - 1);
                    sum += source[entering * stride + column] - source[leaving * stride + column];
                }
            }
            for (var y = 0; y < height; y++) target[y * stride + x * 4 + 3] = 255;
        }
    }
}
