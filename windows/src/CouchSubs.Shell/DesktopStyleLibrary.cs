// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchSubs.Shell;

/// <summary>Kullanıcının adla kaydettiği görünüm (bilgisayar ekranındaki altyazı penceresi için).</summary>
public sealed record SavedDesktopStyle(string Name, DesktopSubtitleStyle Style);

/// <summary>
/// Kaydedilmiş görünümler: ad temizliği, aynı adın üstüne yazma, sınır, seçili görünümün hangi kayda eşit olduğu ve
/// dosyaya dışa/içe aktarma. Kayıtlar her zaman <see cref="DesktopStylePreset.Custom"/> ve geçerli değerlerle tutulur.
/// </summary>
public static class DesktopStyleLibrary
{
    public const int MaxStyles = 30;
    public const int MaxNameLength = 40;
    /// <summary>Dışa aktarılan dosyanın uzantısı ve biçim etiketi.</summary>
    public const string FileExtension = ".couchsubs-style";
    private const string FormatTag = "CouchSubsDesktopStyle";
    /// <summary>Elle düzenlenmiş dosyalar: sondaki virgül ve yorumlar hata sayılmaz.</summary>
    private static readonly JsonSerializerOptions Lenient = new() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Ad: boşluklar kırpılır, denetim karakterleri atılır, en çok <see cref="MaxNameLength"/> karakter; boşsa null.</summary>
    public static string? CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static DesktopSubtitleStyle Stored(DesktopSubtitleStyle style) =>
        style.Normalized() with { Preset = DesktopStylePreset.Custom };

    /// <summary>
    /// Görünümü adla kaydeder: aynı ad (büyük/küçük harf fark etmez) varsa onun yerine geçer, yoksa sona eklenir.
    /// Liste doluysa ya da ad geçersizse liste değişmez.
    /// </summary>
    public static IReadOnlyList<SavedDesktopStyle> Save(IReadOnlyList<SavedDesktopStyle> saved, string? name, DesktopSubtitleStyle style)
    {
        if (CleanName(name) is not { } clean) return saved;
        var entry = new SavedDesktopStyle(clean, Stored(style));
        var index = IndexOf(saved, clean);
        if (index >= 0) return [.. saved.Take(index), entry, .. saved.Skip(index + 1)];
        return saved.Count >= MaxStyles ? saved : [.. saved, entry];
    }

    public static IReadOnlyList<SavedDesktopStyle> Remove(IReadOnlyList<SavedDesktopStyle> saved, string name) =>
        [.. saved.Where(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Görünen görünüm kayıtlılardan birinin aynısıysa o kayıt (ayarlarda adıyla seçili gösterilir).</summary>
    public static SavedDesktopStyle? Matching(IReadOnlyList<SavedDesktopStyle> saved, DesktopSubtitleStyle? style)
    {
        if (style is null || style.Normalized().Preset != DesktopStylePreset.Custom) return null;
        var stored = Stored(style);
        return saved.FirstOrDefault(entry => Stored(entry.Style) == stored);
    }

    private static int IndexOf(IReadOnlyList<SavedDesktopStyle> saved, string name)
    {
        for (var i = 0; i < saved.Count; i++)
            if (string.Equals(saved[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Paylaşılabilir dosya içeriği (okunaklı JSON).</summary>
    public static string Export(SavedDesktopStyle entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(FormatTag, 1);
            writer.WriteString("Name", CleanName(entry.Name) ?? "Style");
            writer.WritePropertyName("Style");
            JsonSerializer.Serialize(writer, Stored(entry.Style));
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Dosyadan görünüm: dışa aktarılmış dosya ya da yalnız görünüm nesnesi. Bozuk JSON ya da görünüm olmayan dosya
    /// null döner; tek tek bozuk alanlar varsayılanla dolar. Adı yoksa <paramref name="fallbackName"/> (dosya adı).
    /// </summary>
    public static SavedDesktopStyle? Import(string json, string fallbackName)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var styleElement = DesktopSubtitleStyleConverter.Find(root, "Style");
            var body = styleElement is { ValueKind: JsonValueKind.Object } inner ? inner
                : DesktopSubtitleStyleConverter.Find(root, FormatTag) is null && LooksLikeStyle(root) ? root
                : (JsonElement?)null;
            if (body is not { } element) return null;
            var style = element.Deserialize<DesktopSubtitleStyle>(Lenient) ?? new DesktopSubtitleStyle();
            var name = CleanName(DesktopSubtitleStyleConverter.Text(root, "Name")) ?? CleanName(fallbackName) ?? "Style";
            return new SavedDesktopStyle(name, Stored(style));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Görünüm nesnesinin bilinen alanlarından en az biri var mı (rastgele JSON görünüm sayılmasın).</summary>
    private static bool LooksLikeStyle(JsonElement root) =>
        new[] { "Preset", "FontFamily", "TextColor", "BackgroundColor", "OutlineThickness", "BackgroundOpacity" }
            .Any(name => DesktopSubtitleStyleConverter.Find(root, name) is not null);
}

/// <summary>
/// Kaydedilmiş görünümler listesini ayar dosyasından toleranslı okur: bozuk öğe atlanır, adı olmayan ya da yinelenen
/// ad atlanır, en çok <see cref="DesktopStyleLibrary.MaxStyles"/> kayıt. Liste bozuksa boş açılır (diğer ayarlar korunur).
/// </summary>
public sealed class SavedDesktopStylesConverter : JsonConverter<IReadOnlyList<SavedDesktopStyle>>
{
    public override bool HandleNull => true;

    public override IReadOnlyList<SavedDesktopStyle> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        IReadOnlyList<SavedDesktopStyle> result = [];
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = DesktopStyleLibrary.CleanName(DesktopSubtitleStyleConverter.Text(item, "Name"));
            if (name is null || result.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var style = DesktopSubtitleStyleConverter.Find(item, "Style") is { } element
                ? element.Deserialize<DesktopSubtitleStyle>() ?? new DesktopSubtitleStyle()
                : new DesktopSubtitleStyle();
            result = DesktopStyleLibrary.Save(result, name, style);
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<SavedDesktopStyle> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("Name", entry.Name);
            writer.WritePropertyName("Style");
            JsonSerializer.Serialize(writer, entry.Style, options);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
