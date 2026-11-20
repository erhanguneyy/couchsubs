// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using CouchSubs.App.Services;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresi (Mac: SubtitleOverlay.swift): kenarsız, her pencerenin üstünde, saydam
/// zeminli; görev çubuğunda görünmez, odağı almaz. "Tıklamalar geçsin" açıkken fare alttaki pencereye (oyun, PS Remote
/// Play) gider. Satırlar TV'ye gidenlerin aynısıdır ve TV uygulamasıyla aynı yığınla (<see cref="SubtitleStack"/>)
/// gösterilir. Yeri ve genişliği ayarlara kaydedilir; altyazı yokken gizlenir.
/// </summary>
public sealed partial class SubtitleOverlayWindow : Window
{
    private const int GwlExStyle = -20;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private readonly AppController controller;
    private readonly SubtitleStack stack = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new();
    private readonly StackPanel linesPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly DesktopRegionsPanel regionsPanel;
    /// <summary>Görünen satırların kutuları; solan satırın animasyonu yeniden çizimde baştan başlamasın.</summary>
    private readonly Dictionary<int, Border> boxes = [];
    private readonly Grid adjustLayer = new() { Visibility = Visibility.Collapsed };
    /// <summary>Pencerenin içeriği; bulanık arka plan bunun boyutuna göre boyanır.</summary>
    private readonly Grid root = new();
    /// <summary>Kaybolan satırlar solarken burada, son yerlerinde durur (satırlar kayınca yerleri değişmez).</summary>
    private readonly Canvas exitLayer = new() { IsHitTestVisible = false };
    /// <summary>Son çizimde görünen satırlar; yalnız yeni gelen satır belirme animasyonuyla girer.</summary>
    private HashSet<int> shownIds = [];
    /// <summary>Başka bir tam ekran ya da üstte duran pencere öne gelince altyazıyı yeniden en üste koyar.</summary>
    private readonly TopmostKeeper topmost;
    private IntPtr handle;
    /// <summary>Pencere bir kez açılamadı ya da çizilemedi: bu oturumda bir daha denenmez, çeviri pencere olmadan sürer.</summary>
    private bool broken;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir.</summary>
    private static double Scale => OverlayPlacement.Scale(SystemParameters.PrimaryScreenHeight);

    public static SubtitleOverlayWindow Attach(AppController controller) => new(controller);

    private SubtitleOverlayWindow(AppController controller)
    {
        this.controller = controller;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Title = "CouchSubs";
        topmost = new TopmostKeeper(this);

        regionsPanel = new DesktopRegionsPanel(linesPanel, 8 * Scale);
        root.Children.Add(adjustLayer);
        root.Children.Add(new Border { Padding = new Thickness(24 * Scale, 0, 24 * Scale, 8 * Scale), Child = linesPanel });
        root.Children.Add(new Border { Padding = new Thickness(24 * Scale, 8 * Scale, 24 * Scale, 0), Child = regionsPanel });
        root.Children.Add(exitLayer);
        BuildAdjustLayer();
        Content = root;
        PlaceFromSettings();

        timer.Tick += (_, _) => Guard("zamanlayıcı", () =>
        {
            timer.Stop();
            Refresh();
        });
        SourceInitialized += (_, _) => Guard("pencere tutamağı", () =>
        {
            handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var style = (long)GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, (IntPtr)OverlayWindowStyle.Base(style));
            ApplyClickThrough();
        });
        // Tıklamalar geçmiyorsa pencere her yerinden sürüklenir.
        MouseLeftButtonDown += (_, e) => Guard("sürükleme", () =>
        {
            if (e.OriginalSource is Thumb or ButtonBase || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { return; /* düğme bu arada bırakıldı */ }
            SaveBounds();
        });
        // Başka DPI'lı ekrana taşındı: bulanıklığın kırpma bölgesi yeni piksel ölçeğiyle yeniden kurulur.
        DpiChanged += (_, _) => Guard("DPI", () =>
        {
            blurRegion = null;
            ApplyBlur(IsVisible);
        });
        controller.OverlayEvent += OnEvent;
        controller.OverlayHidden += OnHidden;
        controller.PropertyChanged += OnChanged;
    }

    public void Detach()
    {
        controller.OverlayEvent -= OnEvent;
        controller.OverlayHidden -= OnHidden;
        controller.PropertyChanged -= OnChanged;
        timer.Stop();
        topmost.Dispose();
        StopBackdrop();
        try { Close(); }
        catch (InvalidOperationException error) { StartupGuard.Log("Altyazı penceresi kapatılamadı", error); }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppController.Settings) or nameof(AppController.OverlayAdjusting))) return;
        Guard("ayar değişikliği", () =>
        {
            // Görünüm değiştiyse kutular yeniden kurulur; başka bir ayar değişince (ör. çeviri motoru) görünen satırlar
            // olduğu gibi kalır, solan satırın animasyonu baştan başlamaz.
            if (e.PropertyName == nameof(AppController.Settings) && LookChanged()) boxes.Clear();
            ApplyClickThrough();
            FitHeight();
            Refresh();
        });
    }

    private void OnEvent(SubtitleEvent e) => Guard("altyazı", () =>
    {
        stack.StackPrevious = controller.Settings.Style.StackPrevious;
        switch (e.Kind)
        {
            case SubtitleEventKind.Subtitle:
                screenUntil = 0;
                stack.Show(e.Text, e.DurationMs, e.Paragraphs, Now, e.Replaces);
                break;
            // "Ekranı çevir": çeviriler satır satır gösterilir. Hemen ardından gelen "temizle" ("Ekran çevriliyor…"
            // yazısı için) onları kaldırmaz; yeniden basmak (ScreenClear) ya da yeni altyazı kaldırır.
            case SubtitleEventKind.Screen:
                stack.Show(e.Text, e.DurationMs, paragraphs: true, Now);
                screenUntil = Now + e.DurationMs / 1000.0;
                break;
            case SubtitleEventKind.ScreenClear:
                if (screenUntil > Now) stack.Hide();
                screenUntil = 0;
                break;
            // Ek alanların çevirisi altyazının üstünde, alanın bulunduğu yana yaslı küçük kutularda durur.
            case SubtitleEventKind.Region:
                if (e.Id is null) break;
                // Kendiliğinden bulunan ekran yazıları (auto-…) bu pencerede gösterilmez: saydam pencere kullanıcının
                // seçtiği altyazı yeridir; oyunun her yerindeki yazılar burada altyazının üstüne yığılıp altyazı
                // "istenmeyen yerden çıkıyor" gibi görünüyordu (beta.63). Onlar TV'de ve yayın penceresinde kendi yerinde.
                if (ScreenTextAreas.IsAuto(e.Id)) break;
                regions.Remove(e.Id);
                if (e.Text.Length > 0) regions[e.Id] = (e.Text, e.Box, e.Blocks);
                break;
            case SubtitleEventKind.RegionClear:
                regions.Clear();
                break;
            default:
                if (screenUntil <= Now) stack.Clear(Now);
                break;
        }
        Refresh();
    });

    /// <summary>"Ekranı çevir" çevirilerinin pencerede kalacağı an (<see cref="Now"/> saatiyle); yoksa 0.</summary>
    private double screenUntil;

    /// <summary>Ek alanların penceredeki çevirileri (kimlik → metin ve oyundaki yeri).</summary>
    private readonly Dictionary<string, (string Text, CouchSubs.Core.TextBox? Box, IReadOnlyList<ScreenTranslation.Item>? Blocks)> regions = [];

    private void OnHidden() => Guard("gizleme", () =>
    {
        stack.Hide();
        regions.Clear();
        Refresh();
    });

    /// <summary>
    /// Penceredeki her iş bundan geçer. Pencere açılamaz ya da çizilemezse hata hata.log'a yazılır, kullanıcıya bir kez
    /// söylenir ve pencere kapatılır; uygulama kapanmaz, çeviri (TV'ye, OBS'ye, dublaja) sürer.
    /// </summary>
    private void Guard(string where, Action action)
    {
        if (broken) return;
        try
        {
            action();
        }
        catch (Exception error)
        {
            broken = true;
            StartupGuard.Log($"Altyazı penceresi ({where})", error);
            try
            {
                timer.Stop();
                if (IsVisible) Hide();
            }
            catch (Exception hideError)
            {
                StartupGuard.Log("Altyazı penceresi gizlenemedi", hideError);
            }
            StartupGuard.Show(Application.Current, StartupGuard.OverlayFailedMessage(error));
        }
    }

    /// <summary>Görünen satırları çizer, pencereyi gösterir ya da gizler, bir sonraki değişime zamanlayıcı kurar.</summary>
    private void Refresh()
    {
        var now = Now;
        var lines = stack.Lines(now).ToList();
        var adjusting = controller.OverlayAdjusting;
        if (lines.Count == 0 && adjusting)
            lines.Add(new SubtitleStack.Line(0, Localizer.Current["Subtitles appear here."], SubtitleStack.Role.Current, false, false));
        adjustLayer.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;

        var look = Resolved;
        var animate = look.Animation != DesktopAnimation.None && look.AnimationDuration > 0;
        var removed = boxes.Keys.Except(lines.Select(l => l.Id)).ToList();
        // Kaybolan satır: animasyon açıksa son yerinde solarak çıkar (pencere görünürken).
        var leaving = animate && IsVisible && controller.ShowsOnComputer
            ? removed.Select(id => boxes[id]).Where(box => box.Parent == linesPanel && box.IsVisible).Select(box => (Box: box, At: box.TranslatePoint(default, root))).ToList()
            : [];
        linesPanel.Children.Clear();
        foreach (var id in removed) boxes.Remove(id);
        foreach (var (box, at) in leaving) Leave(box, at, look);
        foreach (var line in lines)
        {
            if (!boxes.TryGetValue(line.Id, out var box))
            {
                box = MakeBox(line);
                boxes[line.Id] = box;
                if (animate && !shownIds.Contains(line.Id)) BoxAnimation.Enter(box, look, Scale);
            }
            if (line.Fading && box.Tag is not "fading")
            {
                box.Tag = "fading";
                box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(SubtitleStack.PreviousFade)));
            }
            linesPanel.Children.Add(box);
        }

        shownIds = [.. lines.Select(line => line.Id)];
        regionsPanel.ShowRegions(regions, MakeRegionBox);

        var visible = controller.ShowsOnComputer && (lines.Count > 0 || regions.Count > 0 || exitLayer.Children.Count > 0);
        if (visible && !IsVisible) Show();
        else if (!visible && IsVisible) Hide();
        // Arada öne gelen tam ekran/üstte duran pencere altyazıyı örtmüş olabilir; odak alınmadan yeniden üste konur.
        if (visible) topmost.RaiseThrottled();
        ApplyBlur(visible);

        timer.Stop();
        if (stack.NextChange(now) is { } next)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, next - now));
            timer.Start();
        }
    }

    /// <summary>Çizilen görünüm (dondurulmuş fırça, kalem, gölge); görünüm ya da ölçek değişmedikçe aynı kalır.</summary>
    private DesktopLook? look;
    /// <summary>Kutuların kurulduğu ayarlar; bunlar değişmedikçe kutular yeniden kurulmaz.</summary>
    private (DesktopSubtitleStyle Look, SubtitleStyle Tv, bool SpeakerColors)? drawn;

    /// <summary>Penceredeki görünüm: Ayarlar'daki "Bilgisayar ekranında" (TV'yi izliyorsa TV görünümü).</summary>
    private DesktopSubtitleStyle Resolved => DesktopStyles.Resolve(controller.Settings.DesktopStyle, controller.Settings.Style);

    private DesktopLook Look => look = DesktopLook.For(Resolved, Scale, look);

    private bool LookChanged()
    {
        var settings = controller.Settings;
        var now = (Resolved, settings.Style, settings.SpeakerColors);
        if (drawn == now) return false;
        drawn = now;
        return true;
    }

    /// <summary>
    /// Altyazı kutusu (bkz. <see cref="DesktopSubtitleBox"/>): seçili görünümde; TV'yi izliyorsa TV'deki kutunun aynısı
    /// (siyah yarı saydam zemin, beyaz ya da sarı yarı kalın yazı). Üst satırlar soluk.
    /// </summary>
    private Border MakeBox(SubtitleStack.Line line)
    {
        var settings = controller.Settings;
        var current = line.Role == SubtitleStack.Role.Current;
        // Uzun çeviri en çok 3 satıra dengeli bölünür; 3 satıra sığmazsa yazı biraz küçülür (bkz. SubtitleWrap).
        var wrapped = line.Wrapped;
        var box = DesktopSubtitleBox.Make(Look, wrapped.Text, line.Text, current, line.Paragraphs, wrapped.Scale,
            band: settings.Style.BackgroundShape == SubtitleBackgroundShape.Band, settings.SpeakerColors, controller.SpeakerBook);
        box.Opacity = line.Role switch { SubtitleStack.Role.Current => 1, SubtitleStack.Role.Previous => 0.6, _ => 0.45 };
        return box;
    }

    /// <summary>Ek alanın çevirisi: altyazıdan küçük, koyu zeminli kutu; oyundaki yazının bulunduğu yana yaslı.</summary>
    private Border MakeRegionBox(string text, CouchSubs.Core.TextBox? area, IReadOnlyList<ScreenTranslation.Item>? blocks)
    {
        var look = Look;
        var scale = Scale;
        var size = Math.Max(12, look.Style.FontSize * scale * 0.55);
        // Otomatik bulunan ekran yazısı: çeviri özgün yazının stilinde.
        var apart = controller.Settings.SetTextApart;
        if (apart && StyledRegionBox.Make(blocks, size, scale, translucent: true) is { } styled) return styled;
        // Ayrık gösterim: stili bilinmeyen ek alan kutusu altyazı kutusuna benzemesin, zeminsiz ve koyu gölgeli.
        if (apart && StyledRegionBox.Plain(text, size, scale, area) is { } plain) return plain;
        var middle = area is { } box ? (box.Left + box.Right) / 2 : 0.5;
        // Yazı tipi ve rengi altyazınınki gibi; okunur kalsın diye zemin her zaman koyu.
        var label = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = look.Text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = size * 1.25,
        };
        if (look.Family is { } family) label.FontFamily = family;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)),
            CornerRadius = new CornerRadius(8 * scale),
            Padding = new Thickness(14 * scale, 6 * scale, 14 * scale, 6 * scale),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            HorizontalAlignment = middle < 0.4 ? HorizontalAlignment.Left : middle > 0.6 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>Yeri ayarlanırken: kesik çizgili çerçeve, açıklama, "Bitti" ve iki yandan genişletme tutamakları.</summary>
    private void BuildAdjustLayer()
    {
        var accent = Application.Current?.TryFindResource("accentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
        adjustLayer.Children.Add(new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            RadiusX = 14,
            RadiusY = 14,
        });
        var done = new Button { Content = Localizer.Current["Done"], Margin = new Thickness(12, 0, 0, 0) };
        if (Application.Current?.TryFindResource("PrimaryButton") is Style primary) done.Style = primary;
        done.Click += (_, _) => Guard("Bitti", () => controller.OverlayAdjusting = false);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        bar.Children.Add(new TextBlock
        {
            Text = Localizer.Current["Drag to move, drag the edges to resize."],
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        bar.Children.Add(done);
        adjustLayer.Children.Add(bar);
        adjustLayer.Children.Add(MakeHandle(HorizontalAlignment.Left, accent));
        adjustLayer.Children.Add(MakeHandle(HorizontalAlignment.Right, accent));
    }

    /// <summary>Kenardan genişletme: ortası yerinde kalır, iki yana eşit büyür.</summary>
    private Thumb MakeHandle(HorizontalAlignment side, Brush accent)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.BackgroundProperty, accent);
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var thumb = new Thumb
        {
            Width = 6,
            Height = 48,
            Margin = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeWE,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = face },
        };
        thumb.DragDelta += (_, e) => Guard("genişletme", () =>
        {
            var grow = side == HorizontalAlignment.Right ? e.HorizontalChange : -e.HorizontalChange;
            var center = Left + Width / 2;
            if (!double.IsFinite(center)) return;
            // Math.Clamp en büyük değer en küçükten küçükse hata verir; sınırlar OverlayPlacement'ta güvenle hesaplanır.
            var width = OverlayPlacement.ClampWidth(Width + 2 * grow, SystemParameters.VirtualScreenWidth);
            Width = width;
            Left = center - width / 2;
        });
        thumb.DragCompleted += (_, _) => Guard("genişletme", SaveBounds);
        return thumb;
    }

    /// <summary>Kaybolan satırı son yerinde soldurur, bitince kaldırır (pencere boşaldıysa gizlenir).</summary>
    private void Leave(Border box, Point at, DesktopSubtitleStyle look)
    {
        var size = box.RenderSize;
        box.Margin = new Thickness(0);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Width = size.Width;
        box.Height = size.Height;
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        exitLayer.Children.Add(box);
        BoxAnimation.Exit(box, look, () => Guard("kaybolma", () =>
        {
            exitLayer.Children.Remove(box);
            Refresh();
        }));
    }

    /// <summary>Ekrandan yakalanan, şiddeti ayarlanabilir bulanıklık; açık değilse ya da desteklenmiyorsa null.</summary>
    private ScreenBackdrop? backdrop;
    /// <summary>Pencere ekran yakalamasından hariç tutulamadı: bu oturumda Windows'un sabit bulanıklığı kullanılır.</summary>
    private bool captureUnavailable;

    /// <summary>
    /// Arka plan bulanıklığı. Önce ekrandan yakalanan, şiddeti ayarlanabilir bulanıklık (<see cref="ScreenBackdrop"/>):
    /// pencere görünürken çalışır, gizliyken durur. Windows pencereyi yakalamadan hariç tutamıyorsa Windows'un sabit
    /// bulanıklığına düşülür (<see cref="ApplySystemBlur"/>). Kapalıyken pencereye dokunulmaz.
    /// </summary>
    private void ApplyBlur(bool visible)
    {
        if (handle == IntPtr.Zero) return;
        var look = Resolved;
        if (!look.BackgroundBlur) StopBackdrop();
        else if (!captureUnavailable)
        {
            if (backdrop is null && visible)
            {
                backdrop = ScreenBackdrop.TryStart(handle, Dispatcher, frame => Guard("bulanıklık", () => BackdropPainter.Paint(root, frame)));
                captureUnavailable = backdrop is null;
                if (captureUnavailable) StartupGuard.Log("Arka plan bulanıklığı: pencere ekran yakalamasından hariç tutulamadı; Windows bulanıklığı kullanılıyor", null);
            }
            if (backdrop is not null)
            {
                ApplySystemBlur(false, visible);
                var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                backdrop.RadiusPixels = look.BlurRadius * Scale * device;
                backdrop.Running = visible;
                if (visible)
                {
                    // Yeni kutu bir sonraki kareyi beklemeden son bulanık görüntüyle dolsun.
                    UpdateLayout();
                    BackdropPainter.Paint(root, backdrop.Current);
                }
                return;
            }
        }
        ApplySystemBlur(look.BackgroundBlur, visible);
    }

    private void StopBackdrop()
    {
        backdrop?.Dispose();
        backdrop = null;
    }

    /// <summary>Kutunun köşe yarıçapı (katmanlı kutuda iç çerçevenin).</summary>
    private static double CornerOf(FrameworkElement element) => element switch
    {
        Border box when DesktopSubtitleBox.Split(box) is { } parts => parts.Frame.CornerRadius.TopLeft,
        Border border => border.CornerRadius.TopLeft,
        _ => 0,
    };

    /// <summary>Bulanıklık açık mı (pencereye en son uygulanan).</summary>
    private bool blurOn;
    /// <summary>Pencereye en son uygulanan kırpma bölgesi; aynıysa yeniden kurulmaz. null: kırpma yok.</summary>
    private IReadOnlyList<PixelBox>? blurRegion;

    /// <summary>
    /// Yedek: Windows'un sabit bulanıklığı (pencerenin yeri ayarlanmıyorsa); pencere yalnız görünen kutuların şekline
    /// kırpılır. Kapalıyken pencereye dokunulmaz.
    /// </summary>
    private void ApplySystemBlur(bool blur, bool visible)
    {
        var wanted = blur && !controller.OverlayAdjusting;
        if (wanted != blurOn)
        {
            WindowBlur.SetEnabled(handle, wanted);
            blurOn = wanted;
        }
        if (!wanted || !visible)
        {
            if (!wanted && blurRegion is not null)
            {
                WindowBlur.SetRegion(handle, null);
                blurRegion = null;
            }
            return;
        }
        // Yeni kutuların yeri ancak yerleşimden sonra bilinir; bölge çizimden önce hazır olsun diye şimdi yerleştirilir.
        UpdateLayout();
        var device = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var shown = linesPanel.Children.OfType<FrameworkElement>().Concat(exitLayer.Children.OfType<FrameworkElement>()).Concat(regionsPanel.Boxes)
            .Where(element => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element =>
            {
                var bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
                var radius = CornerOf(element);
                return (new ScreenArea(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
            });
        var pixels = OverlayBlurRegion.ToPixels(shown, device.M11, device.M22);
        if (blurRegion is not null && blurRegion.SequenceEqual(pixels)) return;
        WindowBlur.SetRegion(handle, pixels);
        blurRegion = pixels;
    }

    /// <summary>"Tıklamalar geçsin" açıksa (ve yeri ayarlanmıyorsa) fare pencereden geçer.</summary>
    private void ApplyClickThrough()
    {
        if (handle == IntPtr.Zero) return;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        var wanted = OverlayWindowStyle.ClickThrough(style, controller.Settings.OverlayClickThrough, controller.OverlayAdjusting);
        if (wanted != style) SetWindowLongPtr(handle, GwlExStyle, (IntPtr)wanted);
    }


    /// <summary>Pencere en çok üç satıra (ve satırların kaymasına) yetecek kadar yüksektir; alt kenarı sabit kalır.</summary>
    private void FitHeight()
    {
        var height = OverlayPlacement.Height(Resolved.FontSize, Scale);
        if (Math.Abs(Height - height) < 0.5) return;
        var bottom = double.IsNaN(Top) ? double.NaN : Top + Height;
        Height = height;
        if (!double.IsNaN(bottom)) Top = bottom - height;
    }

    /// <summary>
    /// Kayıtlı yer (ekranlardan birinde yeterince görünüyorsa) ya da ana ekranın altında ortada, %70 genişlikte. Kayıt
    /// bozuksa ya da o ekran artık bağlı değilse pencere ekran dışında ya da sıfır genişlikte açılmaz.
    /// </summary>
    private void PlaceFromSettings()
    {
        var height = OverlayPlacement.Height(Resolved.FontSize, Scale);
        var work = SystemParameters.WorkArea;
        var place = OverlayPlacement.Place(controller.Settings.Overlay,
            new ScreenArea(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight),
            new ScreenArea(work.Left, work.Top, work.Width, work.Height), height);
        Height = height;
        Width = place.Width;
        Left = place.Left;
        Top = place.Top;
    }

    /// <summary>NaN ya da sonsuz değer JSON'a yazılamaz (ayar kaydı hata verirdi); öyleyse kaydedilmez.</summary>
    private void SaveBounds()
    {
        if (OverlayPlacement.ToSave(Left, Top, Width) is { } bounds) controller.SaveOverlayBounds(bounds);
    }
}
