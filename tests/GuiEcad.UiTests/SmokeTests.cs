using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace GuiEcad.UiTests;

/// <summary>
/// GuiEcad.App の起動・主要 XAML UI 表示を確認する E2E スモークテスト。
/// 作画キャンバス（Win2D）は UIA 非対応のため対象外。
/// </summary>
public class SmokeTests
{
    [Fact]
    public void アプリが起動しメインウィンドウのタイトルを取得できる()
    {
        using var app = new AppDriver();
        Assert.NotNull(app.MainWindow);
        Assert.Contains("GuiEcad", app.MainWindow.Title);
    }

    [Theory]
    [InlineData("ファイル(F)")]
    [InlineData("編集(E)")]
    [InlineData("表示(V)")]
    [InlineData("ヘルプ(H)")]
    public void トップメニューに主要項目が存在する(string menuName)
    {
        using var app = new AppDriver();
        var item = app.MainWindow.FindFirstDescendant(cf =>
            cf.ByName(menuName).And(cf.ByControlType(ControlType.MenuItem)));
        Assert.NotNull(item);
    }

    [Fact]
    public void ファイルメニューを開くと新規項目が表示される()
    {
        using var app = new AppDriver();

        // フライアウト項目はポップアップ（別ウィンドウ）に出るため OpenMenuItem が
        // デスクトップ全体から探す。
        var newItem = app.OpenMenuItem("ファイル(F)", "新規(N)");
        Assert.NotNull(newItem);

        // メニューを閉じて後始末（開いたままだと Dispose 時の終了を阻害しうる）。
        app.MainWindow.Focus();
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    [Fact]
    public void 接続検査トグルがツールバーに存在する()
    {
        using var app = new AppDriver();
        var toggle = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ConnectivityToggle"));
        Assert.NotNull(toggle);
    }

    [Fact]
    public void テストモードに切り替えるとステータス表示が変わり元に戻せる()
    {
        using var app = new AppDriver();

        // 初期は作画モード。
        Assert.Equal("作画モード", app.StatusModeText);

        // モード切替でツールバーが再構成され要素が stale 化しうるため、毎回取り直す。
        ToggleButton Toggle() => app.MainWindow
            .FindFirstDescendant(cf => cf.ByAutomationId("TestModeBtn"))?.AsToggleButton()
            ?? throw new InvalidOperationException("TestModeBtn が見つかりません。");

        // テストモードへ。ステータスが "テストモード..." 始まりになるまで待つ。
        Toggle().Click();
        Assert.True(Retry.WhileFalse(
            () => app.StatusModeText.StartsWith("テストモード"),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(200)).Result,
            $"テストモードに遷移しませんでした（現在: '{app.StatusModeText}'）。");

        // 作画モードへ戻す。
        Toggle().Click();
        Assert.True(Retry.WhileFalse(
            () => app.StatusModeText == "作画モード",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(200)).Result,
            $"作画モードに戻りませんでした（現在: '{app.StatusModeText}'）。");
    }

    [Fact]
    public void ナビゲーションツリーが表示される()
    {
        // TreeView ルートには AutomationId が反映されないため ControlType で探す。
        using var app = new AppDriver();
        var tree = app.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.NotNull(tree);
    }

    [Fact]
    public void タブビューが表示される()
    {
        // TabView ルートにも AutomationId が反映されないため ControlType で探す。
        using var app = new AppDriver();
        var tabView = app.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tab));
        Assert.NotNull(tabView);
    }

    [Fact]
    public void ツールパレットの選択ツールが既定で選択されている()
    {
        using var app = new AppDriver();
        var select = app.MainWindow
            .FindFirstDescendant(cf => cf.ByAutomationId("BtnSelect"))?.AsRadioButton();
        Assert.NotNull(select);
        Assert.True(select!.IsChecked, "起動直後は選択ツールが選択されているはず。");
    }

    [Fact]
    public void ステータスバーが初期シート表示を出している()
    {
        using var app = new AppDriver();
        var sheet = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("StatusSheet"));
        Assert.NotNull(sheet);
        Assert.Equal("シート 1 / 1", sheet!.Name);
    }

    [Fact]
    public void バージョン情報ダイアログを開いてOKで閉じられる()
    {
        using var app = new AppDriver();

        var about = app.OpenMenuItem("ヘルプ(H)", "バージョン情報...");
        about.AsMenuItem().Click();

        // ContentDialog（Title="GuiEcad"・CloseButtonText="OK"）が出る。
        // ダイアログはポップアップに乗るため Desktop 全体から探す。
        var okButton = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByName("OK").And(cf.ByControlType(ControlType.Button))),
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(300)).Result;
        Assert.NotNull(okButton);

        okButton!.AsButton().Invoke();

        // 閉じたら OK ボタンが消える（消滅＝条件 true になったら成功）。
        Assert.True(Retry.WhileFalse(
            () => app.Automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByName("OK").And(cf.ByControlType(ControlType.Button))) is null,
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(300)).Result,
            "ダイアログが閉じませんでした。");
    }
}
