namespace GuiEcad_App;

/// <summary>
/// アプリ共通のログ出力。出力先は %TEMP%（常に書き込み可・サンドボックス制限を受けない）に一元化する。
/// デバッグログは DEBUG ビルドのみ、クラッシュログは常時出力する。
/// </summary>
internal static class AppLog
{
    private static readonly string DebugLogFile = Path.Combine(Path.GetTempPath(), "guiecad_debug.log");
    private static readonly string CrashLogFile = Path.Combine(Path.GetTempPath(), "guiecad_crash.log");

    /// <summary>デバッグ追記ログ。DEBUG ビルドでのみ %TEMP%\guiecad_debug.log に追記（Release は何もしない）。</summary>
    public static void Debug(string msg)
    {
#if DEBUG
        try { File.AppendAllText(DebugLogFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[AppLog FAIL] {ex.Message}"); }
#endif
    }

    /// <summary>起動失敗など致命的例外を常時 %TEMP%\guiecad_crash.log に記録する（書込失敗は握り潰す）。</summary>
    public static void Crash(Exception ex)
    {
        try { File.WriteAllText(CrashLogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n"); }
        catch { /* ログ失敗は無視 */ }
    }
}
