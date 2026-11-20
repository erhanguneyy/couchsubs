// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using CouchSubs.Core;

namespace CouchSubs.Shell.Tests;

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresinin görünümü: eski ayarlar eskisi gibi açılır, bozuk alanlar yalnız
/// kendileri varsayılana döner, hazır görünümler ve "Varsayılana dön" beklendiği gibi çalışır, TV görünümü değişmez.
/// </summary>
public class DesktopSubtitleStyleTests
{
    private static UserSettings Read(string json) => JsonSerializer.Deserialize<UserSettings>(json)!;

    // --- Göç ve kayıt ---

    [Fact]
    public void OldSettingsFollowTheTvLook()
    {
        var old = Read("""{"SourceLanguage":"en","Style":{"FontSize":58,"Bold":true,"TextColor":"Yellow","Background":40},"GamePacks":false}""");
        Assert.Equal(new DesktopSubtitleStyle(), old.DesktopStyle);
        var shown = DesktopStyles.Resolve(old.DesktopStyle, old.Style);
        Assert.Equal(DesktopStylePreset.MatchTv, shown.Preset);
        Assert.Equal(58, shown.FontSize);
        Assert.Equal(700, shown.FontWeight);
        Assert.Equal(DesktopStyles.YellowText, shown.TextColor);
        Assert.Equal(40, shown.BackgroundOpacity);
        Assert.False(old.GamePacks);
    }

    [Fact]
    public void MatchTvLooksLikeThePreviousWindow()
    {
        var shown = DesktopStyles.Resolve(new DesktopSubtitleStyle(), new SubtitleStyle());
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, shown.FontFamily);
        Assert.Equal(46, shown.FontSize);
        Assert.Equal(600, shown.FontWeight);
        Assert.Equal("#FFFFFF", shown.TextColor);
        Assert.Equal(0, shown.OutlineThickness);
        Assert.Equal(("#000000", 4.0, 100), (shown.ShadowColor, shown.ShadowBlur, shown.ShadowOpacity));
        Assert.Equal(("#000000", 90), (shown.BackgroundColor, shown.BackgroundOpacity));
        Assert.Equal(10, shown.CornerRadius);
        Assert.Equal(DesktopTextAlignment.Center, shown.Alignment);
        Assert.Equal(1.3, shown.LineSpacing);
    }

    [Fact]
    public void CustomStyleIsSavedAndRestored()
    {
        var style = new DesktopSubtitleStyle
        {
            Preset = DesktopStylePreset.Custom, FontFamily = "Arial", FontSize = 60, FontWeight = 700, TextColor = "#00FF00",
            OutlineColor = "#112233", OutlineThickness = 2.5, ShadowColor = "#445566", ShadowBlur = 12, ShadowOpacity = 70,
            BackgroundColor = "#101010", BackgroundOpacity = 35, CornerRadius = 4, Alignment = DesktopTextAlignment.Left, LineSpacing = 1.5,
        };
        var json = JsonSerializer.Serialize(new UserSettings { DesktopStyle = style });
        Assert.Contains("\"Preset\":\"Custom\"", json);
        Assert.Contains("\"Alignment\":\"Left\"", json);
        Assert.Equal(style, Read(json).DesktopStyle);
    }

    [Fact]
    public void ChangedStyleIsSavedAndRestoredAfterRestart()
    {
        var settings = new MemorySettings();
        var controller = AppControllerTests.Make(settings: settings);
        var changed = new List<string?>();
        controller.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var tv = controller.Settings.Style;
        controller.Settings = controller.Settings with { DesktopStyle = DesktopStyles.Preset(DesktopStylePreset.Cinematic, 52) };
        Assert.Contains(nameof(AppController.Settings), changed);
        Assert.Equal(tv, settings.Saved?.Style);
        // Gerçek kayıt gibi JSON'dan geçirilip yeniden açılır.
        var reopened = new MemorySettings();
        reopened.Save(Read(JsonSerializer.Serialize(settings.Saved)));
        var restarted = AppControllerTests.Make(settings: reopened);
        Assert.Equal(DesktopStylePreset.Cinematic, restarted.Settings.DesktopStyle.Preset);
        Assert.Equal(52, DesktopStyles.Resolve(restarted.Settings.DesktopStyle, restarted.Settings.Style).FontSize);
    }

    [Fact]
    public void TvStyleJsonIsUnchanged()
    {
        // TV protokolü: görünüm SubtitleStyle olarak gider; masaüstü görünümü ona eklenmez.
        var json = JsonSerializer.Serialize(new UserSettings().Style);
        Assert.DoesNotContain("Outline", json);
        Assert.DoesNotContain("FontFamily", json);
    }

    // --- Bozuk ya da eksik alanlar ---

    [Fact]
    public void MalformedFieldsFallBackWithoutResettingOtherSettings()
    {
        var settings = Read("""
            {"SourceLanguage":"de","GamePacks":false,"Style":{"FontSize":58},
             "DesktopStyle":{"Preset":"Custom","FontSize":"big","FontWeight":{"x":1},"TextColor":"#GGHHII","OutlineColor":42,
                             "OutlineThickness":-5,"ShadowBlur":1e308,"ShadowOpacity":250,"BackgroundColor":"#abc",
                             "BackgroundOpacity":"55","CornerRadius":null,"Alignment":"Diagonal","LineSpacing":9,"Unknown":true}}
            """);
        Assert.Equal("de", settings.SourceLanguage);
        Assert.False(settings.GamePacks);
        Assert.Equal(58, settings.Style.FontSize);
        var style = settings.DesktopStyle;
        var defaults = new DesktopSubtitleStyle();
        Assert.Equal(DesktopStylePreset.Custom, style.Preset);
        Assert.Equal(defaults.FontSize, style.FontSize);
        Assert.Equal(defaults.FontWeight, style.FontWeight);
        Assert.Equal(defaults.TextColor, style.TextColor);
        Assert.Equal(defaults.OutlineColor, style.OutlineColor);
        Assert.Equal(0, style.OutlineThickness);
        Assert.Equal(DesktopSubtitleStyle.MaxShadowBlur, style.ShadowBlur);
        Assert.Equal(100, style.ShadowOpacity);
        Assert.Equal("#AABBCC", style.BackgroundColor);
        Assert.Equal(55, style.BackgroundOpacity);
        Assert.Equal(defaults.CornerRadius, style.CornerRadius);
        Assert.Equal(DesktopTextAlignment.Center, style.Alignment);
        Assert.Equal(DesktopSubtitleStyle.MaxLineSpacing, style.LineSpacing);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"Cinematic\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void WrongShapeOpensWithTheDefault(string value)
    {
        var settings = Read($$"""{"GamePacks":false,"DesktopStyle":{{value}}}""");
        Assert.Equal(new DesktopSubtitleStyle(), settings.DesktopStyle);
        Assert.False(settings.GamePacks);
    }

    [Fact]
    public void UnknownPresetFromANewerVersionKeepsItsValuesAsCustom()
    {
        var style = Read("""{"DesktopStyle":{"Preset":"Neon","TextColor":"#FF00FF"}}""").DesktopStyle;
        Assert.Equal(DesktopStylePreset.Custom, style.Preset);
        Assert.Equal("#FF00FF", DesktopStyles.Resolve(style, new SubtitleStyle()).TextColor);
    }

    [Fact]
    public void NumericPresetIsNotTrusted() =>
        Assert.Equal(DesktopStylePreset.Custom, Read("""{"DesktopStyle":{"Preset":"3"}}""").DesktopStyle.Preset);

    [Fact]
    public void PropertyNamesAreCaseInsensitive() =>
        Assert.Equal("#123456", Read("""{"DesktopStyle":{"preset":"custom","textcolor":"#123456"}}""").DesktopStyle.TextColor);

    // --- Renk ve yazı tipi doğrulaması ---

    [Theory]
    [InlineData("#ffffff", "#FFFFFF")]
    [InlineData("00ff7f", "#00FF7F")]
    [InlineData(" #AbC ", "#AABBCC")]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("#FF00FF00", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ColorsAreValidated(string? input, string? expected) => Assert.Equal(expected, DesktopStyles.NormalizeColor(input));

    [Fact]
    public void ChannelsOfInvalidColorAreBlack()
    {
        Assert.Equal(((byte)0xFF, (byte)0xE1, (byte)0x35), DesktopStyles.Channels("#FFE135"));
        Assert.Equal(((byte)0, (byte)0, (byte)0), DesktopStyles.Channels("nonsense"));
    }

    [Fact]
    public void MissingFontFallsBackToTheAppFont()
    {
        string[] installed = ["Arial", "Segoe UI"];
        Assert.Equal("Arial", DesktopStyles.AvailableFont("arial", installed));
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, DesktopStyles.AvailableFont("Comic Neue Uninstalled", installed));
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, DesktopStyles.AvailableFont("", installed));
    }

    [Fact]
    public void BrokenFontNamesAreCleared()
    {
        Assert.Equal("Arial", DesktopStyles.CleanFontName("  Arial "));
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, DesktopStyles.CleanFontName("Ari\0al"));
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, DesktopStyles.CleanFontName(new string('x', 500)));
        Assert.Equal(DesktopSubtitleStyle.DefaultFont, DesktopStyles.CleanFontName(null));
    }

    [Fact]
    public void NormalizedClampsEveryValue()
    {
        var style = new DesktopSubtitleStyle
        {
            FontSize = 5, FontWeight = 1234, OutlineThickness = double.NaN, ShadowBlur = -1, ShadowOpacity = -3,
            BackgroundOpacity = 101, CornerRadius = double.PositiveInfinity, LineSpacing = 0.2, Preset = (DesktopStylePreset)99,
            Alignment = (DesktopTextAlignment)7,
        }.Normalized();
        Assert.Equal(DesktopSubtitleStyle.MinFontSize, style.FontSize);
        Assert.Equal(DesktopSubtitleStyle.MaxWeight, style.FontWeight);
        Assert.Equal(0, style.OutlineThickness);
        Assert.Equal(0, style.ShadowBlur);
        Assert.Equal(0, style.ShadowOpacity);
        Assert.Equal(100, style.BackgroundOpacity);
        Assert.Equal(10, style.CornerRadius);
        Assert.Equal(DesktopSubtitleStyle.MinLineSpacing, style.LineSpacing);
        Assert.Equal(DesktopStylePreset.Custom, style.Preset);
        Assert.Equal(DesktopTextAlignment.Center, style.Alignment);
    }

    // --- Hazır görünümler ve varsayılana dönüş ---

    [Theory]
    [InlineData(DesktopStylePreset.Cinematic)]
    [InlineData(DesktopStylePreset.ClassicYellow)]
    [InlineData(DesktopStylePreset.Minimal)]
    [InlineData(DesktopStylePreset.HighContrast)]
    public void PresetKeepsTheTextSize(DesktopStylePreset preset)
    {
        var tv = new SubtitleStyle(FontSize: 72);
        var chosen = DesktopStyles.Select(preset, new DesktopSubtitleStyle(), tv);
        Assert.Equal(preset, chosen.Preset);
        Assert.Equal(72, chosen.FontSize);
        Assert.Equal(chosen, DesktopStyles.Resolve(chosen, tv));
        Assert.Equal(chosen, chosen.Normalized());
    }

    [Fact]
    public void PresetsLookDifferent()
    {
        var looks = DesktopStyles.Presets
            .Where(preset => preset is not (DesktopStylePreset.MatchTv or DesktopStylePreset.Custom))
            .Select(preset => DesktopStyles.Preset(preset, 46) with { Preset = DesktopStylePreset.Custom })
            .ToList();
        Assert.Equal(looks.Count, looks.Distinct().Count());
        Assert.Equal(DesktopStyles.YellowText, DesktopStyles.Preset(DesktopStylePreset.ClassicYellow, 46).TextColor);
        Assert.Equal(100, DesktopStyles.Preset(DesktopStylePreset.HighContrast, 46).BackgroundOpacity);
    }

    [Fact]
    public void NamedPresetIgnoresTamperedValues()
    {
        // Elle düzenlenmiş dosyada hazır görünümün değerleri değiştirilmişse hazır görünüm yine kendisi gibi çizilir.
        var tampered = DesktopStyles.Preset(DesktopStylePreset.Minimal, 50) with { TextColor = "#FF0000" };
        Assert.Equal("#FFFFFF", DesktopStyles.Resolve(tampered, new SubtitleStyle()).TextColor);
    }

    [Fact]
    public void EditingStartsFromWhatIsShownAndBecomesCustom()
    {
        var tv = new SubtitleStyle(FontSize: 58, TextColor: SubtitleTextColor.Yellow);
        var edited = DesktopStyles.Edit(new DesktopSubtitleStyle(), tv, s => s with { OutlineThickness = 2 });
        Assert.Equal(DesktopStylePreset.Custom, edited.Preset);
        Assert.Equal(2, edited.OutlineThickness);
        Assert.Equal(58, edited.FontSize);
        Assert.Equal(DesktopStyles.YellowText, edited.TextColor);

        var fromPreset = DesktopStyles.Edit(DesktopStyles.Preset(DesktopStylePreset.Cinematic, 46), tv, s => s with { CornerRadius = 20 });
        Assert.Equal(DesktopStylePreset.Custom, fromPreset.Preset);
        Assert.Equal(20, fromPreset.CornerRadius);
        Assert.Equal(DesktopStyles.Preset(DesktopStylePreset.Cinematic, 46).OutlineThickness, fromPreset.OutlineThickness);
    }

    [Fact]
    public void InvalidEditIsClampedNotSaved()
    {
        var edited = DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), s => s with { TextColor = "oops", FontSize = 500 });
        Assert.Equal("#FFFFFF", edited.TextColor);
        Assert.Equal(DesktopSubtitleStyle.MaxFontSize, edited.FontSize);
    }

    [Fact]
    public void ChoosingCustomKeepsTheCurrentLook()
    {
        var cinematic = DesktopStyles.Preset(DesktopStylePreset.Cinematic, 46);
        var custom = DesktopStyles.Select(DesktopStylePreset.Custom, cinematic, new SubtitleStyle());
        Assert.Equal(cinematic with { Preset = DesktopStylePreset.Custom }, custom);
    }

    [Fact]
    public void ResetFollowsTheTvAgain()
    {
        var tv = new SubtitleStyle(Bold: true);
        var custom = DesktopStyles.Edit(new DesktopSubtitleStyle(), tv, s => s with { TextColor = "#00FF00" });
        Assert.Equal(new DesktopSubtitleStyle(), DesktopStyles.Select(DesktopStylePreset.MatchTv, custom, tv));
        Assert.Equal(DesktopStyles.FromTv(tv), DesktopStyles.Resolve(DesktopStyles.Select(DesktopStylePreset.MatchTv, custom, tv), tv));
    }

    [Fact]
    public void CustomLookDoesNotFollowLaterTvChanges()
    {
        var custom = DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), s => s with { TextColor = "#00FF00" });
        var shown = DesktopStyles.Resolve(custom, new SubtitleStyle(FontSize: 80, TextColor: SubtitleTextColor.Yellow, Background: 10));
        Assert.Equal("#00FF00", shown.TextColor);
        Assert.Equal(46, shown.FontSize);
        Assert.Equal(90, shown.BackgroundOpacity);
    }

    // --- Çeviri sürerken görünüm değişir ---

    [Fact]
    public void ChangingTheStyleDoesNotHideOrClearSubtitles()
    {
        var controller = AppControllerTests.Make(settings: new MemorySettings());
        controller.Settings = controller.Settings with { Output = SubtitleOutput.Both };
        var events = new List<SubtitleEvent>();
        var hidden = 0;
        controller.OverlayEvent += events.Add;
        controller.OverlayHidden += () => hidden++;
        foreach (var preset in DesktopStyles.Presets)
            controller.Settings = controller.Settings with { DesktopStyle = DesktopStyles.Select(preset, controller.Settings.DesktopStyle, controller.Settings.Style) };
        controller.Settings = controller.Settings with
        {
            DesktopStyle = DesktopStyles.Edit(controller.Settings.DesktopStyle, controller.Settings.Style, s => s with { OutlineThickness = 3 }),
        };
        Assert.Empty(events);
        Assert.Equal(0, hidden);
        Assert.True(controller.ShowsOnComputer);
    }
}

/// <summary>Altyazı penceresinin Win32 biçemleri: tıklama geçirgenliği ayara ve yer ayarına göre; diğer biçemler korunur.</summary>
public class OverlayWindowStyleTests
{
    [Fact]
    public void WindowNeverTakesFocusAndStaysOutOfAltTab()
    {
        Assert.Equal(OverlayWindowStyle.ToolWindow | OverlayWindowStyle.NoActivate, OverlayWindowStyle.Base(0));
        Assert.Equal(0x100 | OverlayWindowStyle.ToolWindow | OverlayWindowStyle.NoActivate, OverlayWindowStyle.Base(0x100));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void ClicksPassOnlyWhenAskedAndNotWhileAdjusting(bool clickThrough, bool adjusting, bool passes)
    {
        var start = OverlayWindowStyle.Base(0);
        foreach (var before in new[] { start, start | OverlayWindowStyle.Transparent })
        {
            var after = OverlayWindowStyle.ClickThrough(before, clickThrough, adjusting);
            Assert.Equal(passes, (after & OverlayWindowStyle.Transparent) != 0);
            Assert.Equal(start, after & ~OverlayWindowStyle.Transparent);
        }
    }
}

/// <summary>Arka plan bulanıklığı: kayıt, eski ayarlar ve pencerenin kutulara kırpılması.</summary>
public class DesktopBlurTests
{
    [Fact]
    public void BlurIsOffForOldSettingsAndTheTvLook()
    {
        Assert.False(JsonSerializer.Deserialize<UserSettings>("""{"DesktopStyle":{"Preset":"Custom"}}""")!.DesktopStyle.BackgroundBlur);
        Assert.False(DesktopStyles.Resolve(new DesktopSubtitleStyle(), new SubtitleStyle()).BackgroundBlur);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    public void BlurIsReadSafely(string value, bool expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<UserSettings>("{\"DesktopStyle\":{\"Preset\":\"Custom\",\"BackgroundBlur\":" + value + "}}")!
            .DesktopStyle.BackgroundBlur);

    [Fact]
    public void BlurIsSavedAndEditable()
    {
        var edited = DesktopStyles.Edit(new DesktopSubtitleStyle(), new SubtitleStyle(), s => s with { BackgroundBlur = true });
        Assert.Equal(DesktopStylePreset.Custom, edited.Preset);
        var json = JsonSerializer.Serialize(new UserSettings { DesktopStyle = edited });
        Assert.Contains("\"BackgroundBlur\":true", json);
        Assert.True(JsonSerializer.Deserialize<UserSettings>(json)!.DesktopStyle.BackgroundBlur);
    }

    [Fact]
    public void MinimalPresetBlurs() => Assert.True(DesktopStyles.Preset(DesktopStylePreset.Minimal, 46).BackgroundBlur);

    [Fact]
    public void BoxesBecomePixelRegionsAtTheScreenScale()
    {
        var pixels = OverlayBlurRegion.ToPixels([(new ScreenArea(10.2, 20.6, 100, 40), 10), (new ScreenArea(-24, 80, 500, 30), 0)], 1.5, 1.5);
        Assert.Equal(new PixelBox(15, 30, 166, 91, 30), pixels[0]);
        Assert.Equal(new PixelBox(-36, 120, 714, 165, 0), pixels[1]);
    }

    [Fact]
    public void CornerNeverExceedsTheBoxAndBrokenBoxesAreSkipped()
    {
        var pixels = OverlayBlurRegion.ToPixels(
            [(new ScreenArea(0, 0, 50, 10), 40), (new ScreenArea(0, 0, 0, 10), 5), (new ScreenArea(double.NaN, 0, 10, 10), 5)], double.NaN, 0);
        Assert.Equal([new PixelBox(0, 0, 50, 10, 10)], pixels);
    }
}
