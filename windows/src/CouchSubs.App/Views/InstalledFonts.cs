// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows.Media;

namespace CouchSubs.App.Views;

/// <summary>Bu bilgisayarda kurulu yazı tipleri (bir kez okunur; ayarlardaki seçici de bunu kullanır).</summary>
internal static class InstalledFonts
{
    private static readonly Lazy<IReadOnlyList<string>> All = new(() =>
    {
        try
        {
            return [.. Fonts.SystemFontFamilies.Select(family => family.Source)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception error) when (error is UriFormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            // Bozuk bir yazı tipi dosyası listeyi okutmazsa uygulamanın yazı tipi kullanılır.
            StartupGuard.Log("Yazı tipleri okunamadı", error);
            return [];
        }
    });

    public static IReadOnlyList<string> Names => All.Value;
}
