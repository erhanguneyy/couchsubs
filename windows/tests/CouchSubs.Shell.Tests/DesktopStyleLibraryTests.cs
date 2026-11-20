// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using CouchSubs.Core;

namespace CouchSubs.Shell.Tests;

/// <summary>Kayıtlı görünümler: ad kuralları, üstüne yazma, sınır, eşleşme, ayar dosyası ve dışa/içe aktarma.</summary>
public class DesktopStyleLibraryTests
{
    private static readonly DesktopSubtitleStyle Green =
        DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), s => s with { TextColor = "#00FF00", BackgroundBlur = true, BlurRadius = 24 });

    [Theory]
    [InlineData("  My style ", "My style")]
    [InlineData("A\u0000B\tC", "ABC")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NamesAreCleaned(string? input, string? expected) => Assert.Equal(expected, DesktopStyleLibrary.CleanName(input));

    [Fact]
    public void LongNamesAreCut() =>
        Assert.Equal(DesktopStyleLibrary.MaxNameLength, DesktopStyleLibrary.CleanName(new string('x', 100))!.Length);

    [Fact]
    public void SavingTheSameNameReplacesIt()
    {
        var saved = DesktopStyleLibrary.Save([], "Night", Green);
        saved = DesktopStyleLibrary.Save(saved, "Day", new DesktopSubtitleStyle());
        saved = DesktopStyleLibrary.Save(saved, "night", Green with { TextColor = "#FF0000" });
        Assert.Equal(["night", "Day"], saved.Select(entry => entry.Name));
        Assert.Equal("#FF0000", saved[0].Style.TextColor);
        Assert.All(saved, entry => Assert.Equal(DesktopStylePreset.Custom, entry.Style.Preset));
    }

    [Fact]
    public void InvalidNameOrFullListLeavesTheListAlone()
    {
        IReadOnlyList<SavedDesktopStyle> saved = [];
        Assert.Same(saved, DesktopStyleLibrary.Save(saved, "  ", Green));
        for (var i = 0; i < DesktopStyleLibrary.MaxStyles; i++) saved = DesktopStyleLibrary.Save(saved, "Style " + i, Green);
        Assert.Same(saved, DesktopStyleLibrary.Save(saved, "One more", Green));
        // Var olanın üstüne yazmak liste doluyken de olur.
        Assert.NotSame(saved, DesktopStyleLibrary.Save(saved, "Style 3", new DesktopSubtitleStyle()));
    }

    [Fact]
    public void RemoveIgnoresCase() =>
        Assert.Empty(DesktopStyleLibrary.Remove(DesktopStyleLibrary.Save([], "Night", Green), "NIGHT"));

    [Fact]
    public void ShownStyleMatchesItsSavedEntry()
    {
        var saved = DesktopStyleLibrary.Save([], "Night", Green);
        Assert.Same(saved[0], DesktopStyleLibrary.Matching(saved, Green));
        Assert.Null(DesktopStyleLibrary.Matching(saved, Green with { BlurRadius = 10 }));
        // Hazır görünüm (değerleri aynı olsa da) kayıtla eşleşmez.
        Assert.Null(DesktopStyleLibrary.Matching(saved, new DesktopSubtitleStyle()));
    }

    [Fact]
    public void SavedStylesSurviveTheSettingsFile()
    {
        var settings = new UserSettings { SavedDesktopStyles = DesktopStyleLibrary.Save([], "Night", Green), DesktopStyle = Green };
        var reopened = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("Night", Assert.Single(reopened.SavedDesktopStyles).Name);
        Assert.Equal(Green.Normalized(), reopened.SavedDesktopStyles[0].Style);
        Assert.NotNull(DesktopStyleLibrary.Matching(reopened.SavedDesktopStyles, reopened.DesktopStyle));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"x\"")]
    public void BrokenListOpensEmpty(string value)
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("{\"GamePacks\":false,\"SavedDesktopStyles\":" + value + "}")!;
        Assert.Empty(settings.SavedDesktopStyles);
        Assert.False(settings.GamePacks);
    }

    [Fact]
    public void BrokenEntriesAreSkipped()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("""
            {"SavedDesktopStyles":[42,{"Name":5},{"Name":"  "},{"Name":"Ok","Style":{"Preset":"Custom","TextColor":"#123456"}},
                                   {"Name":"ok","Style":{}},{"Name":"No style"}]}
            """)!;
        Assert.Equal(["Ok", "No style"], settings.SavedDesktopStyles.Select(entry => entry.Name));
        Assert.Equal("#123456", settings.SavedDesktopStyles[0].Style.TextColor);
    }

    [Fact]
    public void OldSettingsHaveNoSavedStyles() =>
        Assert.Empty(JsonSerializer.Deserialize<UserSettings>("""{"SourceLanguage":"en"}""")!.SavedDesktopStyles);

    // --- Dosya ---

    [Fact]
    public void ExportedFileImportsBack()
    {
        var file = DesktopStyleLibrary.Export(new SavedDesktopStyle("Night", Green));
        Assert.Contains("CouchSubsDesktopStyle", file);
        var imported = DesktopStyleLibrary.Import(file, "ignored")!;
        Assert.Equal("Night", imported.Name);
        Assert.Equal(Green.Normalized(), imported.Style);
    }

    [Fact]
    public void BareStyleObjectImportsWithTheFileName()
    {
        var imported = DesktopStyleLibrary.Import("""{"Preset":"Custom","TextColor":"#ABCDEF", "OutlineThickness": 2,}""", "friend-style")!;
        Assert.Equal("friend-style", imported.Name);
        Assert.Equal("#ABCDEF", imported.Style.TextColor);
        Assert.Equal(DesktopStylePreset.Custom, imported.Style.Preset);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"hello":"world"}""")]
    [InlineData("""{"CouchSubsDesktopStyle":1,"Name":"x"}""")]
    [InlineData("")]
    public void NonStyleFilesAreRejected(string content) => Assert.Null(DesktopStyleLibrary.Import(content, "file"));

    [Fact]
    public void ImportedPresetBecomesCustomWithItsValues()
    {
        var imported = DesktopStyleLibrary.Import("""{"Name":"Cine","Style":{"Preset":"MatchTv","TextColor":"#FF0000"}}""", "f")!;
        Assert.Equal(DesktopStylePreset.Custom, imported.Style.Preset);
        Assert.Equal("#FF0000", imported.Style.TextColor);
    }
}

/// <summary>Gelişmiş görünüm alanları: eski kayıtlar, bozuk değerler, hazır görünümler.</summary>
public class DesktopAdvancedStyleTests
{
    [Fact]
    public void OldSettingsKeepThePlainBox()
    {
        var style = JsonSerializer.Deserialize<UserSettings>("""{"DesktopStyle":{"Preset":"Custom"}}""")!.DesktopStyle;
        Assert.False(style.BackgroundGradient);
        Assert.Equal(0, style.BorderThickness);
        Assert.Equal(DesktopAnimation.None, style.Animation);
        Assert.Equal(0, style.NoiseOpacity);
        Assert.Equal(0, style.BlurBrightness);
    }

    [Fact]
    public void AdvancedFieldsRoundTrip()
    {
        var style = DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), s => s with
        {
            BackgroundBlur = true, BlurRadius = 30, BlurBrightness = -15, NoiseOpacity = 6, BackgroundGradient = true,
            GradientColor = "#112233", GradientAngle = 135, BorderThickness = 1.5, BorderColor = "#AABBCC", BorderOpacity = 40,
            Animation = DesktopAnimation.Zoom, AnimationDuration = 400,
        });
        var json = JsonSerializer.Serialize(new UserSettings { DesktopStyle = style });
        Assert.Contains("\"Animation\":\"Zoom\"", json);
        Assert.Equal(style, JsonSerializer.Deserialize<UserSettings>(json)!.DesktopStyle);
    }

    [Fact]
    public void BrokenAdvancedValuesAreClamped()
    {
        var style = JsonSerializer.Deserialize<UserSettings>("""
            {"DesktopStyle":{"Preset":"Custom","BlurRadius":500,"BlurBrightness":-99,"NoiseOpacity":"lots","GradientColor":"blue",
                             "GradientAngle":-90,"BorderThickness":20,"BorderOpacity":-1,"Animation":"Spin","AnimationDuration":99999}}
            """)!.DesktopStyle;
        Assert.Equal(DesktopSubtitleStyle.MaxBlurRadius, style.BlurRadius);
        Assert.Equal(-DesktopSubtitleStyle.MaxBrightness, style.BlurBrightness);
        Assert.Equal(0, style.NoiseOpacity);
        Assert.Equal(new DesktopSubtitleStyle().GradientColor, style.GradientColor);
        Assert.Equal(270, style.GradientAngle);
        Assert.Equal(DesktopSubtitleStyle.MaxBorderThickness, style.BorderThickness);
        Assert.Equal(0, style.BorderOpacity);
        Assert.Equal(DesktopAnimation.None, style.Animation);
        Assert.Equal(DesktopSubtitleStyle.MaxAnimationMs, style.AnimationDuration);
    }

    [Fact]
    public void MatchTvStaysPlain()
    {
        var shown = DesktopStyles.Resolve(new DesktopSubtitleStyle(), new SubtitleStyle());
        Assert.False(shown.BackgroundBlur || shown.BackgroundGradient);
        Assert.Equal((0.0, DesktopAnimation.None, 0), (shown.BorderThickness, shown.Animation, shown.NoiseOpacity));
    }

    [Fact]
    public void MinimalIsAFrostedCard()
    {
        var minimal = DesktopStyles.Preset(DesktopStylePreset.Minimal, 46);
        Assert.True(minimal.BackgroundBlur);
        Assert.True(minimal.NoiseOpacity > 0 && minimal.BorderThickness > 0);
        Assert.Equal(DesktopAnimation.Slide, minimal.Animation);
    }
}

/// <summary>Arka planın bulanıklaştırılması: küçültme planı ve kutu bulanıklığı.</summary>
public class BackdropBlurTests
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(-5, 1, 0)]
    [InlineData(3, 1, 3)]
    [InlineData(12, 4, 3)]
    [InlineData(80, 8, 10)]
    public void PlanKeepsTheSmallImageCheap(double radius, int downscale, int smallRadius) =>
        Assert.Equal((downscale, smallRadius), BackdropBlur.Plan(radius));

    [Fact]
    public void PlanSurvivesNaN() => Assert.Equal((1, 0), BackdropBlur.Plan(double.NaN));

    [Fact]
    public void FlatImageStaysFlatAndOpaque()
    {
        var pixels = Fill(20, 10, 40, 80, 120, alpha: 0);
        BackdropBlur.Apply(pixels, 20, 10, 3);
        for (var i = 0; i < pixels.Length; i += 4)
            Assert.Equal(((byte)40, (byte)80, (byte)120, (byte)255), (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]));
    }

    [Fact]
    public void SharpEdgeBecomesAGradient()
    {
        const int width = 40, height = 4;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = width / 2; x < width; x++) pixels[(y * width + x) * 4] = 255;
        BackdropBlur.Apply(pixels, width, height, 3);
        var row = Enumerable.Range(0, width).Select(x => (int)pixels[x * 4]).ToList();
        Assert.Equal(0, row[0]);
        Assert.Equal(255, row[^1]);
        Assert.InRange(row[width / 2], 60, 200);
        // Soldan sağa hiç azalmaz (kenar yumuşar ama tersine dönmez).
        Assert.True(row.Zip(row.Skip(1)).All(pair => pair.First <= pair.Second));
    }

    [Fact]
    public void BadSizesAreIgnored()
    {
        var pixels = new byte[16];
        BackdropBlur.Apply(pixels, 0, 4, 3);
        BackdropBlur.Apply(pixels, 10, 10, 3);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    private static byte[] Fill(int width, int height, byte b, byte g, byte r, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, alpha);
        return pixels;
    }
}
