// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CouchSubs.Core;

namespace CouchSubs.Shell;

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresinin hazır görünümleri. <see cref="MatchTv"/>: pencere TV'deki görünümü
/// (<see cref="SubtitleStyle"/>) izler; eski ayar dosyaları ve "Varsayılana dön" bununla açılır, pencere eskisi gibi görünür.
/// </summary>
public enum DesktopStylePreset { MatchTv, Cinematic, ClassicYellow, Minimal, HighContrast, Custom }

/// <summary>Yeni satırın beliriş biçimi; kaybolan satır aynı sürede solar (<see cref="None"/>: önceki sürümlerdeki gibi anında).</summary>
public enum DesktopAnimation { None, Fade, Slide, Zoom }

/// <summary>Altyazı satırlarının yaslanması (çok satırlı "ekranı çevir" listeleri her zaman sola yaslıdır).</summary>
public enum DesktopTextAlignment { Center, Left, Right }

/// <summary>
/// Yalnız Windows'taki altyazı penceresinin görünümü (TV, OBS yayın penceresi, Mac ve Android etkilenmez; TV protokolü
/// değişmez). Boylar ve kalınlıklar TV'deki gibi 1080 satırlık ekrana göredir, pencere ekranın boyuna göre ölçekler.
/// Ayar dosyasında eksik, bozuk ya da yanlış türde alan bütün ayarları sıfırlamaz: o alan varsayılanla dolar
/// (bkz. <see cref="DesktopSubtitleStyleConverter"/>).
/// </summary>
[JsonConverter(typeof(DesktopSubtitleStyleConverter))]
public sealed record DesktopSubtitleStyle
{
    public const int MinFontSize = 16, MaxFontSize = 90;
    public const int MinWeight = 100, MaxWeight = 900;
    public const double MaxOutline = 8, MaxShadowBlur = 30, MaxCornerRadius = 40;
    public const double MinLineSpacing = 1, MaxLineSpacing = 2;
    public const double MaxBlurRadius = 40, MaxBorderThickness = 6;
    public const int MaxBrightness = 50, MaxNoise = 20, MaxAnimationMs = 1000;
    /// <summary>Uygulamanın kendi yazı tipi (Segoe UI Variable); yazı tipi seçilmemiş demektir.</summary>
    public const string DefaultFont = "";

    public DesktopStylePreset Preset { get; init; } = DesktopStylePreset.MatchTv;
    public string FontFamily { get; init; } = DefaultFont;
    public int FontSize { get; init; } = 46;
    /// <summary>OpenType kalınlığı: 400 normal, 600 yarı kalın, 700 kalın.</summary>
    public int FontWeight { get; init; } = 600;
    public string TextColor { get; init; } = "#FFFFFF";
    public string OutlineColor { get; init; } = "#000000";
    /// <summary>Kontur kalınlığı (1080 satırlık ekranda piksel); 0: kontur yok.</summary>
    public double OutlineThickness { get; init; }
    public string ShadowColor { get; init; } = "#000000";
    public double ShadowBlur { get; init; } = 4;
    /// <summary>Gölgenin görünürlüğü, yüzde (0: gölge yok).</summary>
    public int ShadowOpacity { get; init; } = 100;
    public string BackgroundColor { get; init; } = "#000000";
    /// <summary>Zeminin koyuluğu, yüzde (0: zemin yok).</summary>
    public int BackgroundOpacity { get; init; } = 90;
    public double CornerRadius { get; init; } = 10;
    /// <summary>
    /// Kutunun arkasındaki oyun görüntüsü bulanıklaşır (buzlu cam). Zemin rengi ve koyuluğu bulanıklığın üstüne gelir
    /// (akrilik ton). Varsayılan kapalı.
    /// </summary>
    public bool BackgroundBlur { get; init; }
    /// <summary>Bulanıklığın şiddeti (1080 satırlık ekranda piksel).</summary>
    public double BlurRadius { get; init; } = 16;
    /// <summary>Bulanık görüntünün parlaklığı: eksi koyulaştırır, artı açar (yüzde).</summary>
    public int BlurBrightness { get; init; }
    /// <summary>Akrilik doku (ince kumlanma), yüzde; 0: doku yok.</summary>
    public int NoiseOpacity { get; init; }
    /// <summary>Zemin iki renkli gradyan: <see cref="BackgroundColor"/>'dan <see cref="GradientColor"/>'a.</summary>
    public bool BackgroundGradient { get; init; }
    public string GradientColor { get; init; } = "#1E2A44";
    /// <summary>Gradyanın yönü, derece (0: soldan sağa, 90: yukarıdan aşağı).</summary>
    public double GradientAngle { get; init; } = 90;
    /// <summary>Kutunun kenar çizgisi (1080 satırlık ekranda piksel); 0: kenarlık yok.</summary>
    public double BorderThickness { get; init; }
    public string BorderColor { get; init; } = "#FFFFFF";
    public int BorderOpacity { get; init; } = 30;
    public DesktopAnimation Animation { get; init; } = DesktopAnimation.None;
    /// <summary>Beliriş ve kayboluş süresi, milisaniye.</summary>
    public int AnimationDuration { get; init; } = 250;
    public DesktopTextAlignment Alignment { get; init; } = DesktopTextAlignment.Center;
    /// <summary>Satır yüksekliği, yazı boyunun katı.</summary>
    public double LineSpacing { get; init; } = 1.3;

    /// <summary>Bütün değerler sınırlarında, renkler #RRGGBB biçiminde; bozuk değer varsayılanına döner.</summary>
    public DesktopSubtitleStyle Normalized()
    {
        var fallback = Defaults;
        return this with
        {
            Preset = Enum.IsDefined(Preset) ? Preset : DesktopStylePreset.Custom,
            FontFamily = DesktopStyles.CleanFontName(FontFamily),
            FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize),
            FontWeight = Math.Clamp((int)Math.Round(FontWeight / 100.0) * 100, MinWeight, MaxWeight),
            TextColor = DesktopStyles.NormalizeColor(TextColor) ?? fallback.TextColor,
            OutlineColor = DesktopStyles.NormalizeColor(OutlineColor) ?? fallback.OutlineColor,
            OutlineThickness = Clamp(OutlineThickness, 0, MaxOutline, fallback.OutlineThickness),
            ShadowColor = DesktopStyles.NormalizeColor(ShadowColor) ?? fallback.ShadowColor,
            ShadowBlur = Clamp(ShadowBlur, 0, MaxShadowBlur, fallback.ShadowBlur),
            ShadowOpacity = Math.Clamp(ShadowOpacity, 0, 100),
            BackgroundColor = DesktopStyles.NormalizeColor(BackgroundColor) ?? fallback.BackgroundColor,
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100),
            CornerRadius = Clamp(CornerRadius, 0, MaxCornerRadius, fallback.CornerRadius),
            BlurRadius = Clamp(BlurRadius, 0, MaxBlurRadius, fallback.BlurRadius),
            BlurBrightness = Math.Clamp(BlurBrightness, -MaxBrightness, MaxBrightness),
            NoiseOpacity = Math.Clamp(NoiseOpacity, 0, MaxNoise),
            GradientColor = DesktopStyles.NormalizeColor(GradientColor) ?? fallback.GradientColor,
            GradientAngle = double.IsFinite(GradientAngle) ? Math.Round(((GradientAngle % 360) + 360) % 360, 1) : fallback.GradientAngle,
            BorderThickness = Clamp(BorderThickness, 0, MaxBorderThickness, fallback.BorderThickness),
            BorderColor = DesktopStyles.NormalizeColor(BorderColor) ?? fallback.BorderColor,
            BorderOpacity = Math.Clamp(BorderOpacity, 0, 100),
            Animation = Enum.IsDefined(Animation) ? Animation : DesktopAnimation.None,
            AnimationDuration = Math.Clamp(AnimationDuration, 0, MaxAnimationMs),
            Alignment = Enum.IsDefined(Alignment) ? Alignment : DesktopTextAlignment.Center,
            LineSpacing = Clamp(LineSpacing, MinLineSpacing, MaxLineSpacing, fallback.LineSpacing),
        };
    }

    private static readonly DesktopSubtitleStyle Defaults = new();

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value, 2), min, max) : fallback;
}

/// <summary>Hazır görünümler, TV görünümünden türetme ve renk/yazı tipi doğrulaması.</summary>
public static class DesktopStyles
{
    /// <summary>Ayarlarda gösterilen hazır görünümler (sırasıyla).</summary>
    public static IReadOnlyList<DesktopStylePreset> Presets { get; } = Enum.GetValues<DesktopStylePreset>();

    /// <summary>
    /// Pencerede çizilecek görünüm: TV'yi izliyorsa TV ayarlarından (eski pencerenin birebir aynısı), hazır görünümde
    /// onun değerleri (yazı boyu kullanıcınınki kalır), Özel'de kayıtlı değerler. Sonuç her zaman geçerlidir.
    /// </summary>
    public static DesktopSubtitleStyle Resolve(DesktopSubtitleStyle? desktop, SubtitleStyle tv)
    {
        desktop = (desktop ?? new DesktopSubtitleStyle()).Normalized();
        return desktop.Preset switch
        {
            DesktopStylePreset.MatchTv => FromTv(tv),
            DesktopStylePreset.Custom => desktop,
            var preset => Preset(preset, desktop.FontSize),
        };
    }

    /// <summary>TV görünümünün penceredeki karşılığı (önceki sürümlerin çizdiği kutu).</summary>
    public static DesktopSubtitleStyle FromTv(SubtitleStyle tv) => new DesktopSubtitleStyle
    {
        Preset = DesktopStylePreset.MatchTv,
        FontSize = (int)Math.Round(OverlayPlacement.FontSize(tv.FontSize)),
        FontWeight = tv.Bold ? 700 : 600,
        TextColor = tv.TextColor == SubtitleTextColor.Yellow ? YellowText : "#FFFFFF",
        BackgroundOpacity = tv.Background,
    }.Normalized();

    public const string YellowText = "#FFE135";

    /// <summary>Hazır görünümün değerleri; yazı boyu verilene eşit (hazır görünüm boyu değiştirmez).</summary>
    public static DesktopSubtitleStyle Preset(DesktopStylePreset preset, int fontSize) => (preset switch
    {
        DesktopStylePreset.Cinematic => new DesktopSubtitleStyle
        {
            FontFamily = "Segoe UI", FontWeight = 600, TextColor = "#FFFFFF",
            OutlineColor = "#000000", OutlineThickness = 2,
            ShadowColor = "#000000", ShadowBlur = 10, ShadowOpacity = 80,
            BackgroundOpacity = 0, CornerRadius = 0, LineSpacing = 1.25,
            Animation = DesktopAnimation.Fade, AnimationDuration = 300,
        },
        DesktopStylePreset.ClassicYellow => new DesktopSubtitleStyle
        {
            FontFamily = "Arial", FontWeight = 700, TextColor = YellowText,
            OutlineColor = "#000000", OutlineThickness = 2.5,
            ShadowColor = "#000000", ShadowBlur = 3, ShadowOpacity = 100,
            BackgroundOpacity = 0, CornerRadius = 0, LineSpacing = 1.25,
        },
        DesktopStylePreset.Minimal => new DesktopSubtitleStyle
        {
            FontFamily = "Segoe UI", FontWeight = 400, TextColor = "#FFFFFF",
            ShadowColor = "#000000", ShadowBlur = 6, ShadowOpacity = 60,
            BackgroundColor = "#000000", BackgroundOpacity = 35, CornerRadius = 12, LineSpacing = 1.3,
            BackgroundBlur = true, BlurRadius = 20, NoiseOpacity = 3,
            BorderThickness = 1, BorderColor = "#FFFFFF", BorderOpacity = 18,
            Animation = DesktopAnimation.Slide, AnimationDuration = 220,
        },
        DesktopStylePreset.HighContrast => new DesktopSubtitleStyle
        {
            FontFamily = "Segoe UI", FontWeight = 700, TextColor = "#FFFFFF",
            OutlineColor = "#000000", OutlineThickness = 3, ShadowOpacity = 0,
            BackgroundColor = "#000000", BackgroundOpacity = 100, CornerRadius = 0, LineSpacing = 1.35,
        },
        _ => new DesktopSubtitleStyle(),
    } with { Preset = preset, FontSize = fontSize }).Normalized();

    /// <summary>
    /// Hazır görünüm seçildi. TV'yi izle'de pencere yeniden TV'ye uyar; Özel'de o an görünen değerler düzenlenmek
    /// üzere korunur; diğerlerinde hazır değerler gelir. Yazı boyu korunur.
    /// </summary>
    public static DesktopSubtitleStyle Select(DesktopStylePreset preset, DesktopSubtitleStyle? current, SubtitleStyle tv)
    {
        var shown = Resolve(current, tv);
        return preset switch
        {
            DesktopStylePreset.MatchTv => new DesktopSubtitleStyle(),
            DesktopStylePreset.Custom => shown with { Preset = DesktopStylePreset.Custom },
            _ => Preset(preset, shown.FontSize),
        };
    }

    /// <summary>Tek bir değer elle değişti: o an görünen değerlerden yola çıkılır, görünüm Özel olur.</summary>
    public static DesktopSubtitleStyle Edit(DesktopSubtitleStyle? current, SubtitleStyle tv, Func<DesktopSubtitleStyle, DesktopSubtitleStyle> change) =>
        change(Resolve(current, tv) with { Preset = DesktopStylePreset.Custom }).Normalized() with { Preset = DesktopStylePreset.Custom };

    /// <summary>"#RGB", "RRGGBB", "#rrggbb" → "#RRGGBB"; geçersizse null.</summary>
    public static string? NormalizeColor(string? text)
    {
        var hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>"#RRGGBB" rengin kanalları (önce <see cref="NormalizeColor"/> ile doğrulanır; geçersizse siyah).</summary>
    public static (byte R, byte G, byte B) Channels(string color)
    {
        var hex = NormalizeColor(color) ?? "#000000";
        return (byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    /// <summary>Yazı tipi adı: boşluklar kırpılır; denetim karakterli ya da çok uzun ad uygulamanın yazı tipine döner.</summary>
    public static string CleanFontName(string? name)
    {
        var clean = name?.Trim() ?? "";
        return clean.Length > 120 || clean.Any(char.IsControl) ? DesktopSubtitleStyle.DefaultFont : clean;
    }

    /// <summary>Kayıtlı yazı tipi bu bilgisayarda yoksa uygulamanın yazı tipi kullanılır (ad büyük/küçük harfe duyarsız).</summary>
    public static string AvailableFont(string font, IReadOnlyCollection<string> installed) =>
        font.Length == 0 ? DesktopSubtitleStyle.DefaultFont
        : installed.FirstOrDefault(name => string.Equals(name, font, StringComparison.OrdinalIgnoreCase)) ?? DesktopSubtitleStyle.DefaultFont;
}

/// <summary>
/// Ayar dosyasındaki görünümü alan alan okur: yanlış türde ya da bozuk alan yalnız kendisi varsayılana döner (sıradan
/// JSON okuyucusu hata verip bütün ayarları sıfırlardı). Bilinmeyen alanlar yok sayılır.
/// </summary>
public sealed class DesktopSubtitleStyleConverter : JsonConverter<DesktopSubtitleStyle>
{
    /// <summary><c>"DesktopStyle": null</c> da varsayılan görünümle açılır (alan hiçbir zaman null olmaz).</summary>
    public override bool HandleNull => true;

    public override DesktopSubtitleStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var style = new DesktopSubtitleStyle();
        if (root.ValueKind != JsonValueKind.Object) return style;
        // Alan yoksa TV'yi izler; daha yeni bir sürümün tanımadığımız hazır görünümü kayıtlı değerlerle Özel açılır.
        var preset = Find(root, nameof(style.Preset)) is null ? style.Preset
            : Enum(root, nameof(style.Preset), DesktopStylePreset.Custom);
        return new DesktopSubtitleStyle
        {
            Preset = preset,
            FontFamily = Text(root, nameof(style.FontFamily)) ?? style.FontFamily,
            FontSize = (int)Math.Round(Number(root, nameof(style.FontSize), style.FontSize)),
            FontWeight = (int)Math.Round(Number(root, nameof(style.FontWeight), style.FontWeight)),
            TextColor = Text(root, nameof(style.TextColor)) ?? style.TextColor,
            OutlineColor = Text(root, nameof(style.OutlineColor)) ?? style.OutlineColor,
            OutlineThickness = Number(root, nameof(style.OutlineThickness), style.OutlineThickness),
            ShadowColor = Text(root, nameof(style.ShadowColor)) ?? style.ShadowColor,
            ShadowBlur = Number(root, nameof(style.ShadowBlur), style.ShadowBlur),
            ShadowOpacity = (int)Math.Round(Number(root, nameof(style.ShadowOpacity), style.ShadowOpacity)),
            BackgroundColor = Text(root, nameof(style.BackgroundColor)) ?? style.BackgroundColor,
            BackgroundOpacity = (int)Math.Round(Number(root, nameof(style.BackgroundOpacity), style.BackgroundOpacity)),
            CornerRadius = Number(root, nameof(style.CornerRadius), style.CornerRadius),
            BackgroundBlur = Find(root, nameof(style.BackgroundBlur)) is { ValueKind: JsonValueKind.True },
            BlurRadius = Number(root, nameof(style.BlurRadius), style.BlurRadius),
            BlurBrightness = (int)Math.Round(Number(root, nameof(style.BlurBrightness), style.BlurBrightness)),
            NoiseOpacity = (int)Math.Round(Number(root, nameof(style.NoiseOpacity), style.NoiseOpacity)),
            BackgroundGradient = Find(root, nameof(style.BackgroundGradient)) is { ValueKind: JsonValueKind.True },
            GradientColor = Text(root, nameof(style.GradientColor)) ?? style.GradientColor,
            GradientAngle = Number(root, nameof(style.GradientAngle), style.GradientAngle),
            BorderThickness = Number(root, nameof(style.BorderThickness), style.BorderThickness),
            BorderColor = Text(root, nameof(style.BorderColor)) ?? style.BorderColor,
            BorderOpacity = (int)Math.Round(Number(root, nameof(style.BorderOpacity), style.BorderOpacity)),
            Animation = Enum(root, nameof(style.Animation), style.Animation),
            AnimationDuration = (int)Math.Round(Number(root, nameof(style.AnimationDuration), style.AnimationDuration)),
            Alignment = Enum(root, nameof(style.Alignment), style.Alignment),
            LineSpacing = Number(root, nameof(style.LineSpacing), style.LineSpacing),
        }.Normalized();
    }

    public override void Write(Utf8JsonWriter writer, DesktopSubtitleStyle value, JsonSerializerOptions options)
    {
        var style = (value ?? new DesktopSubtitleStyle()).Normalized();
        writer.WriteStartObject();
        writer.WriteString(nameof(style.Preset), style.Preset.ToString());
        writer.WriteString(nameof(style.FontFamily), style.FontFamily);
        writer.WriteNumber(nameof(style.FontSize), style.FontSize);
        writer.WriteNumber(nameof(style.FontWeight), style.FontWeight);
        writer.WriteString(nameof(style.TextColor), style.TextColor);
        writer.WriteString(nameof(style.OutlineColor), style.OutlineColor);
        writer.WriteNumber(nameof(style.OutlineThickness), style.OutlineThickness);
        writer.WriteString(nameof(style.ShadowColor), style.ShadowColor);
        writer.WriteNumber(nameof(style.ShadowBlur), style.ShadowBlur);
        writer.WriteNumber(nameof(style.ShadowOpacity), style.ShadowOpacity);
        writer.WriteString(nameof(style.BackgroundColor), style.BackgroundColor);
        writer.WriteNumber(nameof(style.BackgroundOpacity), style.BackgroundOpacity);
        writer.WriteNumber(nameof(style.CornerRadius), style.CornerRadius);
        writer.WriteBoolean(nameof(style.BackgroundBlur), style.BackgroundBlur);
        writer.WriteNumber(nameof(style.BlurRadius), style.BlurRadius);
        writer.WriteNumber(nameof(style.BlurBrightness), style.BlurBrightness);
        writer.WriteNumber(nameof(style.NoiseOpacity), style.NoiseOpacity);
        writer.WriteBoolean(nameof(style.BackgroundGradient), style.BackgroundGradient);
        writer.WriteString(nameof(style.GradientColor), style.GradientColor);
        writer.WriteNumber(nameof(style.GradientAngle), style.GradientAngle);
        writer.WriteNumber(nameof(style.BorderThickness), style.BorderThickness);
        writer.WriteString(nameof(style.BorderColor), style.BorderColor);
        writer.WriteNumber(nameof(style.BorderOpacity), style.BorderOpacity);
        writer.WriteString(nameof(style.Animation), style.Animation.ToString());
        writer.WriteNumber(nameof(style.AnimationDuration), style.AnimationDuration);
        writer.WriteString(nameof(style.Alignment), style.Alignment.ToString());
        writer.WriteNumber(nameof(style.LineSpacing), style.LineSpacing);
        writer.WriteEndObject();
    }

    /// <summary>Alan adı büyük/küçük harfe duyarsız aranır (elle düzenlenmiş dosyalar).</summary>
    internal static JsonElement? Find(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    internal static string? Text(JsonElement root, string name) =>
        Find(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double Number(JsonElement root, string name, double fallback) => Find(root, name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                                                      && double.IsFinite(number) => number,
        _ => fallback,
    };

    private static T Enum<T>(JsonElement root, string name, T fallback) where T : struct, System.Enum =>
        Text(root, name) is { } text && System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value)
            && !int.TryParse(text, out _)
            ? value
            : fallback;
}
