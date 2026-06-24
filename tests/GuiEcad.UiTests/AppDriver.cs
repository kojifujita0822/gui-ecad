using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace GuiEcad.UiTests;

/// <summary>
/// GuiEcad.App（WinUI3・unpackaged self-contained）を実 exe として起動し、
/// メインウィンドウを掴むテスト用ドライバ。1 テスト＝1 起動で使い捨てる。
/// </summary>
public sealed class AppDriver : IDisposable
{
    private readonly Application _app;
    public UIA3Automation Automation { get; }
    public Window MainWindow { get; }

    public AppDriver()
    {
        var exe = LocateAppExe();
        Automation = new UIA3Automation();
        // unpackaged self-contained なので AUMID ではなく exe 直接起動でよい。
        _app = Application.Launch(exe);

        // WinUI3 は起動直後ウィンドウが未生成のことがあるため、出現を待ってから掴む。
        MainWindow = Retry.WhileNull(
            () => _app.GetMainWindow(Automation, TimeSpan.FromSeconds(2)),
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(300)).Result
            ?? throw new TimeoutException("メインウィンドウを取得できませんでした。");
    }

    /// <summary>ステータスバーの現在モード表示テキスト（"作画モード" / "テストモード..."）。</summary>
    public string StatusModeText =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("StatusMode"))?.Name ?? "";

    /// <summary>
    /// トップメニュー <paramref name="topMenu"/> を開き、出現したフライアウト項目
    /// <paramref name="childItem"/>（別ウィンドウのポップアップ）を返す。
    /// </summary>
    public AutomationElement OpenMenuItem(string topMenu, string childItem)
    {
        var menu = MainWindow.FindFirstDescendant(cf =>
            cf.ByName(topMenu).And(cf.ByControlType(ControlType.MenuItem)))?.AsMenuItem()
            ?? throw new InvalidOperationException($"トップメニュー '{topMenu}' が見つかりません。");

        // MenuBarItem への単発 Click はフォーカス取得だけで開かないことがあるため、
        // 子項目が見えるまで Expand を冪等にリトライする。
        return Retry.WhileNull(
            () =>
            {
                if (menu.Patterns.ExpandCollapse.TryGetPattern(out var ec)
                    && ec.ExpandCollapseState != FlaUI.Core.Definitions.ExpandCollapseState.Expanded)
                    menu.Expand();
                else
                    menu.Click();
                return Automation.GetDesktop().FindFirstDescendant(cf =>
                    cf.ByName(childItem).And(cf.ByControlType(ControlType.MenuItem)));
            },
            timeout: TimeSpan.FromSeconds(6),
            interval: TimeSpan.FromMilliseconds(400)).Result
            ?? throw new InvalidOperationException($"メニュー項目 '{childItem}' が出現しません。");
    }

    /// <summary>
    /// リポジトリルートを上方向に探索し、ビルド済みの GuiEcad.App.exe を見つける。
    /// 複数（x64/x86 等）見つかった場合は最終更新が新しいものを選ぶ。
    /// </summary>
    private static string LocateAppExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GuiEcad.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new FileNotFoundException("リポジトリルート（GuiEcad.sln）が見つかりません。");

        var appBin = Path.Combine(dir.FullName, "src", "GuiEcad.App", "bin");
        if (!Directory.Exists(appBin))
            throw new FileNotFoundException(
                $"App のビルド出力がありません: {appBin}\n" +
                "先に `dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64` を実行してください。");

        var exe = Directory.EnumerateFiles(appBin, "GuiEcad.App.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"GuiEcad.App.exe が {appBin} 配下に見つかりません。先に App をビルドしてください。");
        return exe;
    }

    public void Dispose()
    {
        try
        {
            _app.Close();
            if (!_app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(2)))
                _app.Kill();
        }
        catch { /* 既に終了している場合は無視 */ }
        finally
        {
            _app.Dispose();
            Automation.Dispose();
        }
    }
}
