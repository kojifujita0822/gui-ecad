using System.Numerics;
using System.Text;
using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using GuiEcad_App.Commands;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace GuiEcad_App;

public sealed partial class MainPage
{
    // ===== メニュー =====

    // Undo/Redo の後処理はメニュー・ボタン・アクセラレータで共通（履歴操作→パネル/プロパティ更新→再描画）。
    private void DoUndo() { _history.Undo(); RefreshDevicePanel(); RefreshPropertiesPanel(); Canvas.Invalidate(); }
    private void DoRedo() { _history.Redo(); RefreshDevicePanel(); RefreshPropertiesPanel(); Canvas.Invalidate(); }

    private void OnMenuUndo(object sender, RoutedEventArgs e) => DoUndo();
    private void OnMenuRedo(object sender, RoutedEventArgs e) => DoRedo();

    // Ctrl+Z/Y はフォーカスに依存しない KeyboardAccelerator で処理（ツール選択中でも効く）
    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { DoUndo(); args.Handled = true; }
    private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { DoRedo(); args.Handled = true; }

    // Delete もフォーカス非依存に（ツール選択中・要素/縦コネクタ選択後でも効く）
    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { DeleteSelected(); args.Handled = true; }

    // 配置直後・選択中の要素を Enter で機器名インライン編集（Canvas のフォーカス有無に依らず確実に拾う）
    private void OnEnterAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_testMode || _editingElement is not null || _editingComment is not null || _editingFrame is not null) return;
        if (FindBar.Visibility == Visibility.Visible) return;   // 検索バー入力中は除外
        if (_selected is not null) { ShowDeviceNameEditor(_selected); args.Handled = true; }
    }

    private void OnF2Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_testMode || _editingElement is not null || _editingComment is not null || _editingFrame is not null) return;
        if (FindBar.Visibility == Visibility.Visible) return;
        if (_selected is not null) { ShowCommentEditor(_selected); args.Handled = true; }
    }

    private void OnInsertRowAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_testMode) return;
        _history.Execute(new InsertLastRowCommand(_sheet));
        Canvas.Invalidate();
        args.Handled = true;
    }

    private void OnDeleteRowAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_testMode) return;
        if (_sheet.Grid.Rows <= 1) return;
        _history.Execute(new DeleteLastRowCommand(_sheet));
        Canvas.Invalidate();
        args.Handled = true;
    }

    private void OnInsertRowBtn(object sender, RoutedEventArgs e)
    {
        if (_testMode) return;
        _history.Execute(new InsertLastRowCommand(_sheet));
        Canvas.Invalidate();
    }

    private void OnDeleteRowBtn(object sender, RoutedEventArgs e)
    {
        if (_testMode) return;
        if (_sheet.Grid.Rows <= 1) return;
        _history.Execute(new DeleteLastRowCommand(_sheet));
        Canvas.Invalidate();
    }
    private void OnMenuFind(object sender, RoutedEventArgs e) => ToggleFindBar();

    private void OnMenuNew(object sender, RoutedEventArgs e)
    {
        _document = new LadderDocument();
        var sheet = CreateEmptySheet();
        _document.Sheets.Add(sheet);
        _sheet = sheet;
        _currentPath = null;
        _selected = null;
        _history.Clear();
        _savedUndoDepth = 0;
        _testSessions.Clear();
        _testSession = null;
        _testMode = false;
        TestModeBtn.IsChecked = false;
        StatusMode.Text = "作画モード";
        _findResults.Clear();
        _findIndex = -1;
        RebuildNavTree();
        RebuildOtherPartMenu();
        RefreshDevicePanel();
        Canvas.Invalidate();
    }

    private async void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            // FileTypeFilter は大文字小文字を区別しない。.gcad のみ登録すれば .GCAD も照合される。
            var picker = new FileOpenPicker(GetPickerWindowId());
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".gcad");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var doc = GcadSerializer.Load(file.Path);
            _document = doc;
            if (doc.Sheets.Count == 0) doc.Sheets.Add(new Sheet());
            _sheet = doc.Sheets[0];
            _currentPath = file.Path;
            _selected = null;
            _history.Clear();
            _savedUndoDepth = 0;
            _testSessions.Clear();
            _testSession = _testMode ? GetOrCreateTestSession(_sheet) : null;
            _findResults.Clear();
            _findIndex = -1;
            RebuildNavTree();
            RebuildOtherPartMenu();
            RefreshDevicePanel();
            Canvas.Invalidate();
        }
        catch (Exception ex)
        {
            AppLog($"[OPEN-ERROR] {ex}");
            await ShowErrorAsync(ex.ToString());
        }
    }

    private async void OnMenuSave(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not null)
        {
            try { GcadSerializer.Save(_document, _currentPath); _savedUndoDepth = _history.UndoDepth; UpdateStatusExtras(); }
            catch (Exception ex) { await ShowErrorAsync(ex.Message); }
        }
        else { await SaveAsAsync(); }
    }

    private async void OnMenuSaveAs(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(_document.Info.Title)
            ? "diagram" : _document.Info.Title;
        picker.FileTypeChoices.Add(".GCAD ファイル", new List<string> { ".gcad" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try { GcadSerializer.Save(_document, file.Path); _currentPath = file.Path; _savedUndoDepth = _history.UndoDepth; UpdateStatusExtras(); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void OnMenuExportPdf(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(_document.Info.Title)
            ? "diagram" : _document.Info.Title;
        picker.FileTypeChoices.Add("PDF ファイル", new List<string> { ".pdf" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            // 回路番号を付与してからクロスリファレンスを生成（採番済み Sheet.Lines を参照）
            CircuitNumberer.Number(_document);
            var xref = CrossReferenceBuilder.Build(_document, _document.Library);

            var info = _document.Info;
            bool enableBorder = _document.Settings.EnableBorder;
            int totalPages = _document.Sheets.Count;
            var dr = new DiagramRenderer(DrawingTheme.Default, new RenderOptions());
            using var surface = new PdfRenderSurface(file.Path);
            for (int pi = 0; pi < _document.Sheets.Count; pi++)
            {
                var sheet = _document.Sheets[pi];
                // 最終シートにのみクロスリファレンス一覧表を追加
                bool isLast = pi == _document.Sheets.Count - 1;
                var pageXref = isLast ? xref : null;
                int pageNum = sheet.PageNumber > 0 ? sheet.PageNumber : pi + 1;
                var renderer = surface.BeginPage(dr.PageSize(sheet, pageXref, info, enableBorder));
                dr.Render(renderer, sheet, _document.Library, xref: pageXref, info: info,
                          pageNumber: pageNum, totalPages: totalPages, enableBorder: enableBorder);
                surface.EndPage();
            }

            // 機器表が1件以上あるとき BOM 専用ページを最後に追加する
            var devices = _document.Devices;
            if (devices.ByName.Count > 0)
            {
                int lastColumns = _document.Sheets[^1].Grid.Columns;
                var bomRenderer = surface.BeginPage(dr.BomPageSize(lastColumns, devices.ByName.Count));
                dr.RenderBomPage(bomRenderer, devices, lastColumns);
                surface.EndPage();
            }
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private void OnMenuRestart(object sender, RoutedEventArgs e)
    {
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (exe is null) return;

        // 開発用: ソースの csproj が見つかれば「再ビルドしてから再起動」する。
        // 実行中は exe/DLL がロックされ自分自身をビルドできないため、
        // 外部 PowerShell に「終了待ち→ dotnet build →成功時のみ新 exe 起動」を委譲する。
        string? proj = FindAppProject(exe);
        if (proj is not null)
        {
            try
            {
                string script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guiecad-dev-rebuild.ps1");
                System.IO.File.WriteAllText(script, RebuildScript, new System.Text.UTF8Encoding(true));
                // 起動中 exe と同じ出力ツリーをビルドする（bin\x64\Debug なら Platform=x64、
                // bin\Debug\...\win-x64 なら -r win-x64）。両者を取り違えると古いバイナリが残る。
                string buildArgs = exe.Contains(@"\bin\x64\", StringComparison.OrdinalIgnoreCase)
                    ? "-p:Platform=x64"
                    : "-r win-x64";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
                    $"-WaitPid {Environment.ProcessId} -Proj \"{proj}\" -Exe \"{exe}\" -BuildArgs \"{buildArgs}\"")
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                Application.Current.Exit();
                return;
            }
            catch { /* 失敗時は通常再起動へフォールバック */ }
        }

        // フォールバック: 同じ exe を起動し直すだけ（再ビルドなし）
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Exit();
    }

    /// <summary>exe パスから上位ディレクトリを辿り GuiEcad.App.csproj を探す（開発時のみ存在）。</summary>
    private static string? FindAppProject(string exePath)
    {
        var dir = System.IO.Directory.GetParent(exePath);
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "GuiEcad.App.csproj");
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // 再ビルド＋再起動を行う開発用 PowerShell ヘルパー（再起動時に一時ファイルへ出力して起動）。
    private const string RebuildScript = @"
param([int]$WaitPid, [string]$Proj, [string]$Exe, [string]$BuildArgs = '-p:Platform=x64')
$ErrorActionPreference = 'Continue'
try { Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
$ba = $BuildArgs -split ' '
Write-Host ""==== 再ビルド中 (dotnet build $BuildArgs) ===="" -ForegroundColor Cyan
dotnet build $Proj @ba --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Host 'ビルド成功。アプリを起動します...' -ForegroundColor Green
    Start-Process $Exe
} else {
    Write-Host 'ビルド失敗。上のエラーを確認してください。' -ForegroundColor Red
    Read-Host 'Enter キーで閉じる'
}
";

    private async void OnMenuAbout(object sender, RoutedEventArgs e)
    {
        // バージョンは csproj の <Version> を正典とし、アセンブリから動的取得する。
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string ver = v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        var dialog = new ContentDialog
        {
            Title = "GuiEcad",
            Content = $"ラダー図エディタ\nバージョン {ver}",
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnMenuHowTo(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        void Section(string head, string body)
        {
            panel.Children.Add(new TextBlock
            {
                Text = head,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 4, 0, 0),
            });
            panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }

        Section("モード切替",
            "ツールバーの「テスト」ボタンで作画モードとテストモードを切り替えます。作画モードで図を描き、テストモードでは接点をクリックして動作（通電・励磁）を確認します。");
        Section("作図",
            "左の縦パレットでツール（接点・コイル・端子台など）を選び、作図エリアのセルをクリックして配置します。同じ行で隣り合う要素は自動で横配線され、縦の分岐は「分岐」ツールで列の交点を上から下へドラッグします。");
        Section("機器名・コメント",
            "要素をダブルクリック（または Enter）で機器名を編集、F2 でコメントを編集します。右母線の右側をダブルクリックすると行コメントを入力できます。");
        Section("選択・移動・削除",
            "「選択」ツールで要素・分岐・枠をクリックして選択し、ドラッグで移動、Del キーで削除します。");
        Section("範囲選択・コピー＆貼り付け",
            "「選択」ツールで何もないセルからドラッグすると青い破線の枠で範囲選択でき、枠内の要素がまとめて選択されます。Ctrl+C でコピーし、貼り付けたい位置にマウスを置いて Ctrl+V を押すと、その位置を左上として貼り付きます（機器名はコピー元と同じまま）。貼り付け直後は新しい要素が選択された状態になります。右クリックメニューの「コピー」「貼り付け」からも同じ操作ができ、貼り付けは右クリックした位置に貼り付きます。");
        Section("シート・表示",
            "左のツリーでシートを切り替え、＋／－でシートを追加・削除します。Ctrl + ＋／－ で拡大・縮小、Ctrl+0 で全体表示、スペースキーを押しながらドラッグで画面を移動できます。");
        Section("ファイル・出力",
            "Ctrl+S で保存（.GCAD 形式）、Ctrl+O で開きます。メニューの「PDF出力」で全シートをベクター PDF として出力します。");
        Section("主なショートカット",
            "新規 Ctrl+N ／ 開く Ctrl+O ／ 保存 Ctrl+S\n" +
            "元に戻す Ctrl+Z ／ やり直し Ctrl+Y\n" +
            "検索・置換 Ctrl+F ／ 削除 Del\n" +
            "コピー Ctrl+C ／ 貼り付け Ctrl+V（マウス位置へ）\n" +
            "行追加 Ctrl+Shift+↑ ／ 行削除 Ctrl+Shift+↓\n" +
            "コメント編集 F2 ／ 機器名編集 Enter");

        var dialog = new ContentDialog
        {
            Title = "使い方",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // Microsoft.Windows.Storage.Pickers は WindowId をコンストラクタで受け取る（InitializeWithWindow 不要）。
    // クラシックな Windows.Storage.Pickers と異なり、昇格プロセスやパッケージ ID 起動でも E_FAIL にならない。
    private Microsoft.UI.WindowId GetPickerWindowId()
    {
        var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!);
        return Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
    }

    private async Task ShowErrorAsync(string message)
    {
        // 長い例外メッセージ／空文字でも本文が必ず見えるよう、折返し＋スクロール付きで表示する。
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message) ? "(詳細メッセージなし)" : message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var dialog = new ContentDialog
        {
            Title = "エラー",
            Content = new ScrollViewer { Content = text, MaxHeight = 360 },
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();
    }

}
