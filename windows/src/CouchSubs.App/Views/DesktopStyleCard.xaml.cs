// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CouchSubs.Core;
using CouchSubs.Shell;
using Microsoft.Win32;

namespace CouchSubs.App.Views;

/// <summary>
/// Ayarlardaki "Bilgisayar ekranında" bölümü: yalnız bu bilgisayardaki altyazı penceresinin görünümü
/// (<see cref="DesktopSubtitleStyle"/>). Hazır ve kayıtlı görünümler (kaydet, sil, dışa/içe aktar), örnek oyun sahnesi
/// üstünde canlı önizleme (bulanıklık ve animasyon dahil), renk örnekleri ve HEX alanları, yazı tipi seçici, değerli
/// kaydırıcılar ve "Varsayılana dön". Altyazı yalnız TV'de gösteriliyorsa görünmez.
/// </summary>
public partial class DesktopStyleCard : UserControl
{
    /// <summary>Önizlemedeki kutu, 1080 satırlık ekranın yarısı boyunda çizilir (kart genişliğine sığar).</summary>
    private const double PreviewScale = 0.5;
    /// <summary>İçe aktarılan dosya en çok bu kadar olabilir (görünüm dosyası birkaç yüz bayttır).</summary>
    private const long MaxImportBytes = 256 * 1024;

    private static readonly string[] Swatches = ["#FFFFFF", DesktopStyles.YellowText, "#7FDBFF", "#9BE564", "#FF6B6B", "#C792EA", "#808080", "#000000"];

    private AppController? controller;
    private bool loading;
    private bool subscribed;
    private bool fontsLoaded;
    private DesktopLook? previewLook;
    /// <summary>Önizlemenin kendi konuşan renkleri: örnek "Arthur" oturumdaki gerçek konuşanların renklerini değiştirmesin.</summary>
    private readonly SpeakerColorBook previewSpeakers = new();
    private IReadOnlyList<SavedDesktopStyle>? listedStyles;
    /// <summary>Önizleme sahnesinin bulanık hâli; boyut ya da şiddet değişince yeniden üretilir.</summary>
    private (BitmapSource Image, int Width, int Height, double Radius)? sceneBlur;
    private readonly ColorEditor textColor, outlineColor, shadowColor, backgroundColor, gradientColor, borderColor;

    private sealed record FontOption(string Name, string Label, FontFamily? Family);

    public DesktopStyleCard()
    {
        InitializeComponent();
        textColor = new ColorEditor(TextColorRow, hex => Edit(s => s with { TextColor = hex }));
        outlineColor = new ColorEditor(OutlineColorRow, hex => Edit(s => s with { OutlineColor = hex }));
        shadowColor = new ColorEditor(ShadowColorRow, hex => Edit(s => s with { ShadowColor = hex }));
        backgroundColor = new ColorEditor(BackgroundColorRow, hex => Edit(s => s with { BackgroundColor = hex }));
        gradientColor = new ColorEditor(GradientColorRow, hex => Edit(s => s with { GradientColor = hex }));
        borderColor = new ColorEditor(BorderColorRow, hex => Edit(s => s with { BorderColor = hex }));
        // Ayarlardaki bölümler değişince kart görsel ağaçtan çıkıp yeniden girer; abonelik buna göre açılıp kapanır.
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) =>
        {
            if (controller is not null && subscribed) controller.PropertyChanged -= OnChanged;
            subscribed = false;
        };
    }

    internal void Attach(AppController attached)
    {
        controller = attached;
        Subscribe();
    }

    private void Subscribe()
    {
        if (controller is null || subscribed) return;
        controller.PropertyChanged += OnChanged;
        subscribed = true;
        Refresh();
    }

    private static string PresetName(DesktopStylePreset preset) => Localizer.Current[preset switch
    {
        DesktopStylePreset.MatchTv => "Same as on the TV",
        DesktopStylePreset.Cinematic => "Cinematic",
        DesktopStylePreset.ClassicYellow => "Classic yellow",
        DesktopStylePreset.Minimal => "Minimal",
        DesktopStylePreset.HighContrast => "High contrast",
        _ => "Custom",
    }];

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.Settings)) Refresh();
    }

    /// <summary>Hazır görünümler, ayraç ve kayıtlı görünümler (liste değişince yeniden kurulur).</summary>
    private void ListStyles(IReadOnlyList<SavedDesktopStyle> saved)
    {
        if (ReferenceEquals(listedStyles, saved)) return;
        listedStyles = saved;
        PresetChoice.Items.Clear();
        foreach (var preset in DesktopStyles.Presets)
            PresetChoice.Items.Add(new ComboBoxItem { Content = PresetName(preset), Tag = preset });
        if (saved.Count == 0) return;
        PresetChoice.Items.Add(new ComboBoxItem { Content = new Separator(), IsEnabled = false, Focusable = false });
        foreach (var entry in saved) PresetChoice.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry });
    }

    /// <summary>Denetimleri ve önizlemeyi o an penceredeki görünüme göre doldurur.</summary>
    private void Refresh()
    {
        if (controller is null) return;
        Visibility = controller.ShowsOnComputer ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility != Visibility.Visible) return;
        var settings = controller.Settings;
        var shown = DesktopStyles.Resolve(settings.DesktopStyle, settings.Style);
        var savedMatch = DesktopStyleLibrary.Matching(settings.SavedDesktopStyles, settings.DesktopStyle);
        loading = true;
        try
        {
            ListStyles(settings.SavedDesktopStyles);
            PresetChoice.SelectedItem = PresetChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => savedMatch is not null
                ? ReferenceEquals(item.Tag, savedMatch)
                : Equals(item.Tag, shown.Preset));
            DeleteStyleButton.IsEnabled = savedMatch is not null;
            ResetButton.IsEnabled = settings.DesktopStyle != new DesktopSubtitleStyle();
            LoadFonts();
            var font = DesktopStyles.AvailableFont(shown.FontFamily, InstalledFonts.Names);
            FontChoice.SelectedItem = FontChoice.Items.OfType<FontOption>()
                .FirstOrDefault(option => string.Equals(option.Name, font, StringComparison.OrdinalIgnoreCase));
            var culture = CultureInfo.CurrentCulture;
            Set(SizeSlider, SizeValue, shown.FontSize, shown.FontSize.ToString(culture));
            Set(WeightSlider, WeightValue, shown.FontWeight, shown.FontWeight.ToString(culture));
            Set(SpacingSlider, SpacingValue, shown.LineSpacing, shown.LineSpacing.ToString("0.00", culture));
            Set(OutlineSlider, OutlineValue, shown.OutlineThickness, shown.OutlineThickness.ToString("0.#", culture));
            Set(BlurSlider, BlurValue, shown.ShadowBlur, shown.ShadowBlur.ToString("0", culture));
            Set(ShadowOpacitySlider, ShadowOpacityValue, shown.ShadowOpacity, Percent(shown.ShadowOpacity));
            Set(BackgroundSlider, BackgroundValue, shown.BackgroundOpacity, Percent(shown.BackgroundOpacity));
            Set(CornerSlider, CornerValue, shown.CornerRadius, shown.CornerRadius.ToString("0", culture));
            Set(BlurRadiusSlider, BlurRadiusValue, shown.BlurRadius, shown.BlurRadius.ToString("0", culture));
            Set(BrightnessSlider, BrightnessValue, shown.BlurBrightness, shown.BlurBrightness.ToString("+0;-0;0", culture));
            Set(NoiseSlider, NoiseValue, shown.NoiseOpacity, Percent(shown.NoiseOpacity));
            Set(AngleSlider, AngleValue, shown.GradientAngle, shown.GradientAngle.ToString("0", culture) + "°");
            Set(BorderSlider, BorderValue, shown.BorderThickness, shown.BorderThickness.ToString("0.#", culture));
            Set(BorderOpacitySlider, BorderOpacityValue, shown.BorderOpacity, Percent(shown.BorderOpacity));
            Set(DurationSlider, DurationValue, shown.AnimationDuration, shown.AnimationDuration.ToString(culture) + " ms");
            (shown.Alignment switch
            {
                DesktopTextAlignment.Left => AlignLeft,
                DesktopTextAlignment.Right => AlignRight,
                _ => AlignCenter,
            }).IsChecked = true;
            (shown.Animation switch
            {
                DesktopAnimation.Fade => AnimationFade,
                DesktopAnimation.Slide => AnimationSlide,
                DesktopAnimation.Zoom => AnimationZoom,
                _ => AnimationNone,
            }).IsChecked = true;
            DurationSlider.IsEnabled = shown.Animation != DesktopAnimation.None;
            PlayButton.IsEnabled = shown.Animation != DesktopAnimation.None && shown.AnimationDuration > 0;
            BlurChoice.IsChecked = shown.BackgroundBlur;
            BlurOptions.Visibility = shown.BackgroundBlur ? Visibility.Visible : Visibility.Collapsed;
            GradientChoice.IsChecked = shown.BackgroundGradient;
            GradientOptions.Visibility = shown.BackgroundGradient ? Visibility.Visible : Visibility.Collapsed;
            textColor.Show(shown.TextColor);
            outlineColor.Show(shown.OutlineColor);
            shadowColor.Show(shown.ShadowColor);
            backgroundColor.Show(shown.BackgroundColor);
            gradientColor.Show(shown.GradientColor);
            borderColor.Show(shown.BorderColor);
        }
        finally
        {
            loading = false;
        }
        ShowPreview(shown, settings);
    }

    private static string Percent(int value) => (value / 100.0).ToString("P0", CultureInfo.CurrentCulture);

    private static void Set(System.Windows.Controls.Primitives.RangeBase slider, TextBlock label, double value, string text)
    {
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        label.Text = text;
    }

    /// <summary>Yazı tipleri ilk gösterilişte bir kez okunur (bilgisayardaki bütün yazı tiplerini taramak zaman alır).</summary>
    private void LoadFonts()
    {
        if (fontsLoaded) return;
        fontsLoaded = true;
        FontChoice.Items.Add(new FontOption(DesktopSubtitleStyle.DefaultFont, Localizer.Current["App font"], null));
        foreach (var name in InstalledFonts.Names) FontChoice.Items.Add(new FontOption(name, name, new FontFamily(name)));
    }

    // --- Önizleme ---

    /// <summary>
    /// Pencerenin kutusunun aynısı: üstte soluk, konuşan adlı bir önceki satır (konuşana göre renk), altta seçili yazı
    /// renginde yeni satır. Bulanıklık açıksa örnek sahnenin kutuların arkasına düşen parçası bulanık görünür.
    /// </summary>
    private void ShowPreview(DesktopSubtitleStyle shown, UserSettings settings)
    {
        previewLook = DesktopLook.For(shown, PreviewScale, previewLook);
        var band = settings.Style.BackgroundShape == SubtitleBackgroundShape.Band;
        PreviewLines.Children.Clear();
        var previous = SubtitleWrap.Fit(Localizer.Current["Arthur: Stay close, we're almost there."]);
        var current = Localizer.Current["Subtitles appear here."];
        var top = DesktopSubtitleBox.Make(previewLook, previous.Text, previous.Text, current: false, paragraphs: false, previous.Scale, band,
            settings.SpeakerColors, previewSpeakers);
        top.Opacity = 0.6;
        PreviewLines.Children.Add(top);
        PreviewLines.Children.Add(DesktopSubtitleBox.Make(previewLook, current, current, current: true, paragraphs: false, 1, band,
            settings.SpeakerColors, previewSpeakers));
        PaintPreviewBlur();
    }

    private void OnPreviewSize(object sender, SizeChangedEventArgs e) => PaintPreviewBlur();

    /// <summary>Örnek sahne, pencerede ekrandan yakalanan görüntüyle aynı yöntemle (küçült, kutu bulanıklığı) bulanıklaşır.</summary>
    private void PaintPreviewBlur()
    {
        if (previewLook is not { Style.BackgroundBlur: true } look || PreviewRoot.ActualWidth < 1 || PreviewRoot.ActualHeight < 1) return;
        var (downscale, radius) = BackdropBlur.Plan(look.Style.BlurRadius * PreviewScale);
        var width = Math.Max(1, (int)Math.Ceiling(PreviewRoot.ActualWidth / downscale));
        var height = Math.Max(1, (int)Math.Ceiling(PreviewRoot.ActualHeight / downscale));
        if (sceneBlur is not { } cached || cached.Width != width || cached.Height != height || !cached.Radius.Equals(look.Style.BlurRadius))
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(PreviewScene) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var pixels = new byte[width * height * 4];
            target.CopyPixels(pixels, width * 4, 0);
            BackdropBlur.Apply(pixels, width, height, radius);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            sceneBlur = (image, width, height, look.Style.BlurRadius);
        }
        PreviewRoot.UpdateLayout();
        BackdropPainter.Paint(PreviewRoot, sceneBlur.Value.Image);
    }

    private void OnPlayAnimation(object sender, RoutedEventArgs e)
    {
        if (previewLook is null) return;
        foreach (var box in PreviewLines.Children.OfType<Border>()) BoxAnimation.Enter(box, previewLook.Style, PreviewScale);
    }

    // --- Değişiklikler ---

    private void Update(Func<UserSettings, UserSettings> change)
    {
        if (!loading && controller is not null) controller.Settings = change(controller.Settings);
    }

    /// <summary>Tek değer değişti: görünüm Özel olur, o an görünen değerlerden yola çıkılır.</summary>
    private void Edit(Func<DesktopSubtitleStyle, DesktopSubtitleStyle> change) =>
        Update(s => s with { DesktopStyle = DesktopStyles.Edit(s.DesktopStyle, s.Style, change) });

    private void OnPreset(object sender, SelectionChangedEventArgs e)
    {
        switch (PresetChoice.SelectedItem)
        {
            case ComboBoxItem { Tag: DesktopStylePreset preset }:
                Update(s => s with { DesktopStyle = DesktopStyles.Select(preset, s.DesktopStyle, s.Style) });
                break;
            case ComboBoxItem { Tag: SavedDesktopStyle entry }:
                Update(s => s with { DesktopStyle = entry.Style });
                break;
        }
    }

    /// <summary>Varsayılan: pencere yeniden TV'deki görünümü izler (önceki sürümlerin görünümü). Kayıtlı görünümler kalır.</summary>
    private void OnReset(object sender, RoutedEventArgs e) => Update(s => s with { DesktopStyle = new DesktopSubtitleStyle() });

    private void OnFont(object sender, SelectionChangedEventArgs e)
    {
        if (FontChoice.SelectedItem is FontOption option) Edit(s => s with { FontFamily = option.Name });
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string field } box) return;
        var on = box.IsChecked == true;
        Edit(field switch
        {
            "BackgroundBlur" => s => s with { BackgroundBlur = on },
            "BackgroundGradient" => s => s with { BackgroundGradient = on },
            _ => s => s,
        });
    }

    private void OnAlignment(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<DesktopTextAlignment>(tag, out var alignment))
            Edit(s => s with { Alignment = alignment });
    }

    private void OnAnimation(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<DesktopAnimation>(tag, out var animation))
            Edit(s => s with { Animation = animation });
    }

    private void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || controller is null || sender is not Slider { Tag: string field }) return;
        var value = e.NewValue;
        var whole = (int)Math.Round(value);
        Edit(field switch
        {
            "FontSize" => s => s with { FontSize = whole },
            "FontWeight" => s => s with { FontWeight = whole },
            "LineSpacing" => s => s with { LineSpacing = value },
            "OutlineThickness" => s => s with { OutlineThickness = value },
            "ShadowBlur" => s => s with { ShadowBlur = value },
            "ShadowOpacity" => s => s with { ShadowOpacity = whole },
            "BackgroundOpacity" => s => s with { BackgroundOpacity = whole },
            "CornerRadius" => s => s with { CornerRadius = value },
            "BlurRadius" => s => s with { BlurRadius = value },
            "BlurBrightness" => s => s with { BlurBrightness = whole },
            "NoiseOpacity" => s => s with { NoiseOpacity = whole },
            "GradientAngle" => s => s with { GradientAngle = value },
            "BorderThickness" => s => s with { BorderThickness = value },
            "BorderOpacity" => s => s with { BorderOpacity = whole },
            "AnimationDuration" => s => s with { AnimationDuration = whole },
            _ => s => s,
        });
    }

    // --- Kayıtlı görünümler ---

    private void ShowMessage(string? text)
    {
        LibraryMessage.Text = text ?? "";
        LibraryMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private SavedDesktopStyle? SelectedSaved => controller is null ? null
        : DesktopStyleLibrary.Matching(controller.Settings.SavedDesktopStyles, controller.Settings.DesktopStyle);

    /// <summary>Görünen görünümün adı (kayıtlıysa kendi adı, hazır görünümse onun adı).</summary>
    private string CurrentName()
    {
        if (SelectedSaved is { } saved) return saved.Name;
        if (controller is null) return "";
        var preset = DesktopStyles.Resolve(controller.Settings.DesktopStyle, controller.Settings.Style).Preset;
        return preset == DesktopStylePreset.Custom ? "" : PresetName(preset);
    }

    private void OnSaveStyle(object sender, RoutedEventArgs e)
    {
        ShowMessage(null);
        StyleName.Text = CurrentName();
        SaveStylePanel.Visibility = Visibility.Visible;
        StyleName.Focus();
        StyleName.SelectAll();
    }

    private void OnCancelSave(object sender, RoutedEventArgs e) => SaveStylePanel.Visibility = Visibility.Collapsed;

    private void OnStyleNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConfirmSave(sender, e);
        else if (e.Key == Key.Escape) SaveStylePanel.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmSave(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        if (DesktopStyleLibrary.CleanName(StyleName.Text) is not { } name)
        {
            StyleName.Focus();
            return;
        }
        var settings = controller.Settings;
        var style = DesktopStyles.Resolve(settings.DesktopStyle, settings.Style) with { Preset = DesktopStylePreset.Custom };
        var saved = DesktopStyleLibrary.Save(settings.SavedDesktopStyles, name, style);
        if (ReferenceEquals(saved, settings.SavedDesktopStyles))
        {
            ShowMessage(Localizer.Current.Format("The style list is full (%lld). Delete one first.", DesktopStyleLibrary.MaxStyles));
            return;
        }
        SaveStylePanel.Visibility = Visibility.Collapsed;
        Update(s => s with { SavedDesktopStyles = saved, DesktopStyle = style });
    }

    private void OnDeleteStyle(object sender, RoutedEventArgs e)
    {
        if (SelectedSaved is not { } entry) return;
        ShowMessage(null);
        Update(s => s with { SavedDesktopStyles = DesktopStyleLibrary.Remove(s.SavedDesktopStyles, entry.Name) });
    }

    private static string FileFilter =>
        $"{Localizer.Current["CouchSubs style"]} (*{DesktopStyleLibrary.FileExtension})|*{DesktopStyleLibrary.FileExtension}|JSON (*.json)|*.json";

    private void OnExportStyle(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        ShowMessage(null);
        var settings = controller.Settings;
        var name = CurrentName() is { Length: > 0 } current ? current : Localizer.Current["Custom"];
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter,
            FileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + DesktopStyleLibrary.FileExtension,
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var style = DesktopStyles.Resolve(settings.DesktopStyle, settings.Style);
            File.WriteAllText(dialog.FileName, DesktopStyleLibrary.Export(new SavedDesktopStyle(name, style)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupGuard.Log("Görünüm dışa aktarılamadı", error);
            ShowMessage(Localizer.Current["The file could not be saved."]);
        }
    }

    private void OnImportStyle(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        ShowMessage(null);
        var dialog = new OpenFileDialog { Filter = FileFilter + "|*.*|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SavedDesktopStyle? entry;
        try
        {
            if (new FileInfo(dialog.FileName).Length > MaxImportBytes)
            {
                ShowMessage(Localizer.Current["This file is not a CouchSubs style."]);
                return;
            }
            entry = DesktopStyleLibrary.Import(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupGuard.Log("Görünüm içe aktarılamadı", error);
            ShowMessage(Localizer.Current["The file could not be read."]);
            return;
        }
        if (entry is null)
        {
            ShowMessage(Localizer.Current["This file is not a CouchSubs style."]);
            return;
        }
        var saved = DesktopStyleLibrary.Save(controller.Settings.SavedDesktopStyles, entry.Name, entry.Style);
        if (ReferenceEquals(saved, controller.Settings.SavedDesktopStyles))
            ShowMessage(Localizer.Current.Format("The style list is full (%lld). Delete one first.", DesktopStyleLibrary.MaxStyles));
        Update(s => s with { SavedDesktopStyles = saved, DesktopStyle = entry.Style });
    }

    /// <summary>
    /// Renk satırı: hazır renk örnekleri ve HEX alanı. Geçersiz HEX kaydedilmez, alan kırmızı çerçeveyle işaretlenir;
    /// yazılırken alan ayarlardan gelen değerle ezilmez.
    /// </summary>
    private sealed class ColorEditor
    {
        private readonly System.Windows.Controls.TextBox hex = new() { Width = 96, MaxLength = 7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly List<(Button Button, string Color)> swatches = [];
        private readonly Action<string> changed;
        private bool showing;

        public ColorEditor(ContentControl host, Action<string> changed)
        {
            this.changed = changed;
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var color in Swatches)
            {
                var swatch = new Button
                {
                    Width = 26,
                    Height = 26,
                    MinHeight = 0,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 2, 6, 2),
                    Cursor = Cursors.Hand,
                    ToolTip = color,
                    Template = SwatchTemplate(),
                    Background = new SolidColorBrush(DesktopLook.ToColor(color)),
                };
                AutomationName(swatch, color);
                swatch.Click += (_, _) => changed(color);
                swatches.Add((swatch, color));
                row.Children.Add(swatch);
            }
            hex.TextChanged += (_, _) =>
            {
                if (showing) return;
                var valid = DesktopStyles.NormalizeColor(hex.Text);
                hex.ClearValue(Control.BorderBrushProperty);
                if (valid is null)
                {
                    if (hex.Text.Trim().TrimStart('#').Length >= 6) hex.SetResourceReference(Control.BorderBrushProperty, "dangerBrush");
                    return;
                }
                if (hex.Text.Trim().TrimStart('#').Length == 6) changed(valid);
            };
            // Alan bırakılınca kayıtlı (geçerli) renge döner.
            hex.LostKeyboardFocus += (_, _) => hex.ClearValue(Control.BorderBrushProperty);
            row.Children.Add(hex);
            host.Content = row;
        }

        public void Show(string color)
        {
            foreach (var (button, value) in swatches)
                button.BorderBrush = value == color ? (Application.Current?.TryFindResource("accentBrush") as Brush ?? Brushes.Orange) : Brushes.Transparent;
            if (hex.IsKeyboardFocusWithin && DesktopStyles.NormalizeColor(hex.Text) == color) return;
            showing = true;
            hex.Text = color;
            hex.ClearValue(Control.BorderBrushProperty);
            showing = false;
        }

        private static void AutomationName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        /// <summary>Yuvarlak renk örneği; seçili olanın çevresinde vurgu halkası.</summary>
        private static ControlTemplate SwatchTemplate()
        {
            var ring = new FrameworkElementFactory(typeof(Border));
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush))
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ring.SetValue(Border.PaddingProperty, new Thickness(2));
            var dot = new FrameworkElementFactory(typeof(Border));
            dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            dot.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dot.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)));
            dot.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background))
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ring.AppendChild(dot);
            return new ControlTemplate(typeof(Button)) { VisualTree = ring };
        }
    }
}
