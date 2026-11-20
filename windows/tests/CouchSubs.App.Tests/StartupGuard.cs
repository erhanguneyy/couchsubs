// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.App;

/// <summary>Uygulamanın hata kaydının test karşılığı (bağlantıyla derlenen dosyalar bunu çağırır).</summary>
internal static class StartupGuard
{
    public static List<string> Logged { get; } = [];

    internal static void Log(string message, Exception? error) => Logged.Add(message);
}
