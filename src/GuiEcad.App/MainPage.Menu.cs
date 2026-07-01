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
    private async void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { await SaveCurrentAsync(); args.Handled = true; }

    // Delete もフォーカス非依存に（ツール選択中・要素/縦コネクタ選択後でも効く）。
    // ただしインライン編集中・検索バー入力中はテキスト編集に委ね、要素を消さない
    // （args.Handled を立てずに return し、TextBox に Delete を処理させる）。
    private void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // テキスト入力中（インライン編集・プロパティパネル・検索バー）は要素を消さず、テキスト編集に委ねる。
        if (IsInlineEditing || IsTextInputFocused()) return;
        DeleteSelected();
        args.Handled = true;
    }

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

    private async void OnMenuNew(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardIfDirtyAsync()) return;
        var doc = new LadderDocument();
        doc.Sheets.Add(CreateEmptySheet());
        ApplyNewDocument(doc, markDirty: false);
    }

    private async void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardIfDirtyAsync()) return;
        // FileTypeFilter は大文字小文字を区別しない。.gcad のみ登録すれば .GCAD も照合される。
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".gcad");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await LoadFileAsync(file.Path);
    }

    /// <summary>パスを指定してファイルを読み込む。D&amp;D・起動引数からも呼ばれる。未保存確認は呼び出し元で行うこと。
    /// 自動保存(オートセーブ)ファイルが本体より新しければ、読込前に復元するか確認する。</summary>
    internal async Task LoadFileAsync(string path)
    {
        try
        {
            string autosavePath = AutosavePath(path);
            if (File.Exists(autosavePath) && File.Exists(path)
                && File.GetLastWriteTimeUtc(autosavePath) > File.GetLastWriteTimeUtc(path))
            {
                bool restore = await ConfirmRestoreAutosaveAsync(File.GetLastWriteTimeUtc(autosavePath));
                if (restore)
                {
                    var restored = GcadSerializer.Load(autosavePath);
                    ApplyLoadedDocument(restored, path, markDirty: true);
                    File.Delete(autosavePath);
                    return;
                }
                File.Delete(autosavePath);   // 復元しない場合は次回以降誤検知しないよう削除
            }

            var doc = GcadSerializer.Load(path);
            ApplyLoadedDocument(doc, path, markDirty: false);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[OPEN-ERROR] {ex}");
            await ShowErrorAsync(ex.Message);
        }
    }

    /// <summary>読み込んだドキュメントを反映する共通処理（通常の読込・オートセーブ復元の両方から使う）。</summary>
    private void ApplyLoadedDocument(LadderDocument doc, string path, bool markDirty)
    {
        if (doc.Sheets.Count == 0) doc.Sheets.Add(new Sheet());
        _document = doc;
        _sheet = doc.Sheets[0];
        _currentPath = path;
        _selected = null;
        _history.Clear();
        _savedUndoDepth = 0;
        _testSessions.Clear();
        _testSession = _testMode ? GetOrCreateTestSession(_sheet) : null;
        _find.Clear();
        RebuildNavTree();
        RebuildOtherPartMenu();
        RefreshDevicePanel();
        if (markDirty) MarkDirty();
        Canvas.Invalidate();
    }

    private async void OnMenuSave(object sender, RoutedEventArgs e) => await SaveCurrentAsync();

    private async void OnMenuSaveAs(object sender, RoutedEventArgs e) => await SaveAsAsync();

    /// <summary>現在のドキュメントを保存する。保存できたら true、ユーザーがキャンセル／失敗したら false。</summary>
    private async Task<bool> SaveCurrentAsync()
    {
        if (_currentPath is not null)
        {
            try
            {
                GcadSerializer.Save(_document, _currentPath);
                _savedUndoDepth = _history.UndoDepth;
                UpdateStatusExtras();
                DeleteAutosaveIfExists(_currentPath);   // 本体を保存できたのでオートセーブの控えは不要
                return true;
            }
            catch (Exception ex) { await ShowErrorAsync(ex.Message); return false; }
        }
        return await SaveAsAsync();
    }

    /// <summary>名前を付けて保存。保存できたら true、ピッカーをキャンセル／失敗したら false。</summary>
    private async Task<bool> SaveAsAsync()
    {
        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(_document.Info.Title)
            ? "diagram" : _document.Info.Title;
        picker.FileTypeChoices.Add(".GCAD ファイル", new List<string> { ".gcad" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;

        try
        {
            GcadSerializer.Save(_document, file.Path);
            _currentPath = file.Path;
            _savedUndoDepth = _history.UndoDepth;
            UpdateStatusExtras();
            DeleteAutosaveIfExists(file.Path);
            return true;
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); return false; }
    }

    /// <summary>未保存変更があれば保存／破棄／キャンセルを確認する。続行してよいなら true、中止なら false。
    /// New/Open のほか、ウィンドウクローズ（MainWindow）からも呼ぶため internal。</summary>
    internal async Task<bool> ConfirmDiscardIfDirtyAsync()
    {
        if (!IsDirty) return true;
        var dialog = new ContentDialog
        {
            Title = "未保存の変更",
            Content = "変更を保存しますか？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "破棄",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveCurrentAsync(),   // 保存できたら続行
            ContentDialogResult.Secondary => true,                     // 破棄して続行
            _ => false,                                                // キャンセル＝中止
        };
    }

    private async void OnMenuPreviewPdf(object sender, RoutedEventArgs e)
    {
        CircuitNumberer.Number(_document);
        var xref = CrossReferenceBuilder.Build(_document, _document.Library);

        var dialog = new PdfPreviewDialog(_document, xref, _document.Settings.EnableBorder)
        {
            XamlRoot = this.XamlRoot,
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
            OnMenuExportPdf(sender, e);
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
            var dr = new DiagramRenderer(DrawingTheme.Default, new RenderOptions());
            using var surface = new PdfRenderSurface(file.Path);

            // 物理ページ総数（枠あり時は長い図面を RowsPerPage 行ごとに複数ページへ分割する。
            // 主回路シートは mm ベースの内容の広がりも加味する＝RenderPageCount）。
            int totalPages = enableBorder
                ? _document.Sheets.Sum(dr.RenderPageCount)
                : _document.Sheets.Count;

            int physical = 0;
            foreach (var sheet in _document.Sheets)
            {
                // クロスリファレンス表はシート図面には描かず、専用ページに分ける（最小2ページ）。
                int pages = enableBorder ? dr.RenderPageCount(sheet) : 1;
                for (int p = 0; p < pages; p++)
                {
                    physical++;
                    var renderer = surface.BeginPage(dr.PageSize(sheet, null, info, enableBorder));
                    dr.Render(renderer, sheet, _document.Library, xref: null, info: info,
                              pageNumber: physical, totalPages: totalPages, enableBorder: enableBorder,
                              pageRowStart: p * DiagramRenderer.RowsPerPage,
                              pageRowCount: enableBorder ? DiagramRenderer.RowsPerPage : int.MaxValue);
                    surface.EndPage();
                }
            }

            // クロスリファレンス一覧表を専用ページ（A4縦）として追加する。
            // 表が 1 ページに収まらない場合は複数の A4縦ページへ分割する。
            int crPages = dr.CrossRefPageCount(xref);
            for (int cp = 0; cp < crPages; cp++)
            {
                var crRenderer = surface.BeginPage(dr.CrossRefPageSize());
                dr.RenderCrossRefPage(crRenderer, xref, cp);
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

#if DEBUG
        // 開発用（DEBUG ビルド限定。配布版には含めない）:
        // ソースの csproj が見つかれば「再ビルドしてから再起動」する。
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
#endif

        // フォールバック（Release はこちらのみ）: 同じ exe を起動し直すだけ（再ビルドなし）
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Exit();
    }

#if DEBUG
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
#endif

    private async void OnMenuAbout(object sender, RoutedEventArgs e)
    {
        // バージョンは csproj の <Version> を正典とし、アセンブリから動的取得する。
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var v = asm.GetName().Version;
        string ver = v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";

        // CHANGELOG.md を埋め込みリソースから読み込む（[Unreleased] セクションは除外）。
        string changelog = "";
        using (var stream = asm.GetManifestResourceStream("CHANGELOG.md"))
        {
            if (stream is not null)
            {
                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var raw = reader.ReadToEnd();
                // [Unreleased] と区切り行を除いて実リリース部分だけ残す。
                var sb = new System.Text.StringBuilder();
                bool skip = false;
                foreach (var line in raw.Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed.StartsWith("## [Unreleased]")) { skip = true; continue; }
                    if (trimmed.StartsWith("## [") && trimmed != "## [Unreleased]") skip = false;
                    if (skip || trimmed.StartsWith("<!-- ") || trimmed.StartsWith("[Unreleased]:")
                             || trimmed.StartsWith("[1.") || trimmed == "---") continue;
                    sb.AppendLine(trimmed);
                }
                changelog = sb.ToString().Trim();
            }
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 480,
            Content = new TextBlock
            {
                Text = changelog,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                IsTextSelectionEnabled = true,
            },
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = $"ラダー図エディタ  バージョン {ver}",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "© 2026 FK TEQUNO",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        if (!string.IsNullOrEmpty(changelog))
        {
            panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 4),
                Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            });
            panel.Children.Add(new TextBlock
            {
                Text = "リリースノート",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
            });
            panel.Children.Add(scroll);
        }

        var dialog = new ContentDialog
        {
            Title = "GuiEcad",
            Content = panel,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnMenuHowTo(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 4 };

        void Header(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 10, 0, 2),
            });
        }

        void Body(string text)
        {
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }

        void BulletList(string[] items)
        {
            var bp = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var item in items)
                bp.Children.Add(new TextBlock { Text = "・" + item, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(bp);
        }

        void ShortcutTable((string key, string desc)[] rows)
        {
            var grid = new Grid { Margin = new Thickness(4, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keyTb  = new TextBlock { Text = rows[i].key,  FontSize = 12, Margin = new Thickness(0, 1, 0, 1) };
                var descTb = new TextBlock { Text = rows[i].desc, FontSize = 12, Margin = new Thickness(0, 1, 0, 1) };
                Grid.SetRow(keyTb,  i); Grid.SetColumn(keyTb,  0);
                Grid.SetRow(descTb, i); Grid.SetColumn(descTb, 1);
                grid.Children.Add(keyTb);
                grid.Children.Add(descTb);
            }
            panel.Children.Add(grid);
        }

        Header("モード切替");
        Body("ツールバーの「テスト」ボタンで作画モードとテストモードを切り替えます。作画モードで図を描き、テストモードでは接点をクリックして動作（通電・励磁）を確認します。");

        Header("作図");
        Body("左の縦パレットでツール（接点・コイル・端子台など）を選び、作図エリアのセルをクリックして配置します。同じ行で隣り合う要素は自動で横配線され、縦の分岐は「分岐」ツールで列の交点を上から下へドラッグします。");

        Header("機器名・コメント");
        BulletList([
            "要素をダブルクリックまたは Enter → 機器名を編集",
            "F2 → コメントを編集",
            "右母線の右側をダブルクリック → 行コメントを入力",
        ]);

        Header("選択・移動・削除");
        BulletList([
            "「選択」ツールで要素・分岐・枠をクリック → 選択",
            "選択後ドラッグ → 移動、Del → 削除",
            "何もない場所からドラッグ → 青い破線の範囲選択",
            "範囲選択後 Ctrl+C でコピー、Ctrl+V でマウス位置へ貼り付け",
            "右クリックメニューからもコピー・貼り付け・行追加・削除が可能",
        ]);

        Header("シート・表示");
        BulletList([
            "左のツリーでシートを切り替え、＋／－でシートを追加・削除",
            "Ctrl+＋／－ で拡大・縮小、Ctrl+0 で全体表示",
            "スペースキーを押しながらドラッグで画面を移動",
        ]);

        Header("ファイル・出力");
        Body("Ctrl+S で保存（.GCAD 形式）、Ctrl+O で開きます。メニューの「PDF出力」で全シートをベクター PDF として出力します。");

        Header("主なショートカット");
        ShortcutTable([
            ("Ctrl+N / O / S", "新規 / 開く / 保存"),
            ("Ctrl+Z / Y", "元に戻す / やり直し"),
            ("Ctrl+C / V", "コピー / 貼り付け（マウス位置へ）"),
            ("Ctrl+F", "検索・置換"),
            ("Ctrl+Shift+↑ / ↓", "行追加 / 行削除"),
            ("Ctrl++ / - / 0", "拡大 / 縮小 / 全体表示"),
            ("Del", "選択要素の削除"),
            ("Enter", "選択要素の機器名編集"),
            ("F2", "選択要素のコメント編集"),
            ("Space+ドラッグ", "画面パン"),
            ("Esc", "選択ツールへ戻る"),
        ]);

        var dialog = new ContentDialog
        {
            Title = "使い方",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600, MinWidth = 460 },
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        await ShowDialogAsync(dialog);
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
