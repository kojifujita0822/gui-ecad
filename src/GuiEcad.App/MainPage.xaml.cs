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

public sealed partial class MainPage : Page
{
    private const double DipsPerMm = 96.0 / 25.4;

    // ドキュメント / シート
    private LadderDocument _document;
    private Sheet _sheet;
    private string? _currentPath;
    private readonly GridGeometry _geo = new(9.0, 15.0);

    // 図形フォルダ管理（実フォルダ「図形/」「図形/自作/」のマスター・呼び出し元）
    private PartFolderStore _folderStore = null!;
    private MenuBarItem _shapeMenuItem = null!;   // 「図形(G)」。再構築時に丸ごと差し替える
    private IReadOnlyList<PartFolderEntry> _folderEntries = Array.Empty<PartFolderEntry>();
    private readonly Dictionary<string, PartFolderEntry> _folderPartMap = new();

    // 表示状態
    private bool _connectivityCheck;
    private bool _testMode;
    private bool _devicePanelVisible;
    private double _zoom = 1.6;
    private double _panX = 20, _panY = 20;

    // 作画状態
    private ElementKind? _placeKind;
    private string? _placePartId;   // 配置対象が自作パーツのとき PartLibrary の Id（組込み種別なら null）
    private ElementInstance? _selected;
    private ElementInstance? _moving;
    private GridPos _moveStartPos;
    private bool _panning;
    private Point _lastPointer;

    // 自前ダブルクリック検出（CapturePointer が DoubleTapped を抑制するため、ツール非依存で編集を起動）
    private const double DoubleClickMs = 400;
    private DateTime _lastClickTime;
    private Point _lastClickPos;

    // 縦コネクタ（並列分岐）配置：列境界をドラッグして TopRow〜BottomRow を作る
    private bool _placeConnector;
    private int? _connStartRow;
    private double _connBoundary;   // 0.5 刻み（セル中央にも縦コネクタを置ける）
    private int _connCurRow;
    private VerticalConnector? _selectedConnector;

    // Undo/Redo
    private readonly CommandHistory _history = new();

    // テストモード（_testSession は現在シートのセッション。_testSessions が各シートの状態を保持）
    private TestSession? _testSession;
    private readonly Dictionary<Sheet, TestSession> _testSessions = new();
    private string? _testPressedDevice;

    // 機器名インライン編集
    private ElementInstance? _editingElement;
    // コメントインライン編集 (F2)
    private ElementInstance? _editingComment;
    // 縦コネクタドラッグ移動
    private VerticalConnector? _movingConnector;
    private double _connMoveStartColumn;
    // 行コメントインライン編集
    private RungComment? _editingRungComment;

    // Dirty フラグ（保存時の UndoDepth を記録して比較）
    private int _savedUndoDepth;

    // 設置場所枠（GroupFrame）
    private bool _placeFrame;
    // 枠ドラッグ作成は mm 座標で自由配置（グリッドにスナップしない）
    private (double X, double Y)? _frameStartMm;
    private (double X, double Y) _frameCurMm;
    private GroupFrame? _selectedFrame;
    private GroupFrame? _editingFrame;
    // 枠ドラッグ移動（mm 座標で自由移動）
    private bool _movingFrame;
    private double _moveFrameOriginX, _moveFrameOriginY; // ドラッグ開始時の枠 VisualXMm/YMm
    private double _moveFrameClickX, _moveFrameClickY;   // クリック時のワールド座標

    // スペースキーパン
    private bool _spacePanActive;

    // 範囲選択（Ctrl+C / Ctrl+V 用）
    private bool _rangeSelecting;
    private GridPos _rangeStart;
    private GridPos _rangeEnd;
    private HashSet<ElementInstance> _selectedSet = new();

    // コピー・ペースト
    private sealed record ClipboardData(List<ElementInstance> Elements, int OriginRow, int OriginCol);
    private ClipboardData? _clipboard;

    // 検索・置換（全シート横断）
    private List<(Sheet Sheet, ElementInstance El)> _findResults = new();
    private int _findIndex = -1;

    // 右クリックコンテキストメニュー（CanvasControl は通常の UIElement なので RightTapped で捕捉）
    private bool _menuShowing;                        // 二重 ShowAt 防止フラグ

    // ナビゲーションツリー
    private bool _suppressNavEvents;
    private readonly Dictionary<TreeViewNode, Sheet> _sheetNodeMap = new();

    // 下部出力パネル (P3)
    private List<Diagnostic> _lastDrcResults = new();
    private bool _outputPanelCollapsed;
    private const double OutputPanelDefaultHeight = 130;

    public MainPage()
    {
        InitializeComponent();
        _document = new LadderDocument();
        _document.Sheets.Add(BuildSampleSheet());
        _sheet = _document.Sheets[0];
        Loaded += async (_, _) =>
        {
            await LoadToolIconsAsync();
            RebuildNavTree();
            InitShapeFolder();        // _folderEntries を先に用意（その他▼の自作図形に必要）
            RebuildOtherPartMenu();
        };
    }

    // ===== SVG ツールバーアイコン =====

    private async Task LoadToolIconsAsync()
    {
        var items = new (ElementKind kind, Image img)[]
        {
            (ElementKind.ContactNO,      IconContactNO),
            (ElementKind.ContactNC,      IconContactNC),
            (ElementKind.PushButtonNO,   IconPushButtonNO),
            (ElementKind.PushButtonNC,   IconPushButtonNC),
            (ElementKind.TimerContactNO, IconTimerContactNO),
            (ElementKind.TimerContactNC, IconTimerContactNC),
            (ElementKind.Coil,           IconCoil),
            (ElementKind.Lamp,           IconLamp),
            (ElementKind.Terminal,       IconTerminal),
        };
        foreach (var (kind, img) in items)
            img.Source = await SvgToImageSourceAsync(SvgRenderer.GenerateSymbolSvg(kind));
    }

    private static async Task<SvgImageSource> SvgToImageSourceAsync(string svg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(svg);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var source = new SvgImageSource();
        await source.SetSourceAsync(stream);
        return source;
    }

    // ===== 描画 =====

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        double scale = DipsPerMm * _zoom;
        var transform = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation((float)_panX, (float)_panY);

        var renderer = new Win2DRenderer(args.DrawingSession, transform);
        var dr = new DiagramRenderer(DrawingTheme.Default, new RenderOptions { ConnectivityCheck = _connectivityCheck });
        dr.Render(renderer, _sheet, _document.Library, _testSession?.State);

        // 検索ハイライト（現在シートの一致のみ描画）
        for (int i = 0; i < _findResults.Count; i++)
        {
            if (_findResults[i].Sheet != _sheet) continue;
            DrawElementHighlight(renderer, _findResults[i].El,
                i == _findIndex ? new Color(200, 255, 140, 0) : new Color(100, 255, 220, 0));
        }

        // 選択ハイライト
        foreach (var e in _selectedSet)
            DrawElementHighlight(renderer, e, new Color(80, 0, 120, 255));

        if (_rangeSelecting)
        {
            int r1 = Math.Min(_rangeStart.Row, _rangeEnd.Row);
            int r2 = Math.Max(_rangeStart.Row, _rangeEnd.Row);
            int c1 = Math.Min(_rangeStart.Column, _rangeEnd.Column);
            int c2 = Math.Max(_rangeStart.Column, _rangeEnd.Column);
            double rx = _geo.X(c1), ry = _geo.YRow(r1) - _geo.CellMm * 0.5;
            double rw = (c2 - c1 + 1) * _geo.CellMm, rh = (r2 - r1 + 1) * _geo.CellMm;
            renderer.DrawRectangle(new(rx, ry, rw, rh),
                new StrokeStyle(DrawingTheme.Blue, 0.2, LineStyle.Dashed));
        }

        if (_selected is not null) DrawSelection(renderer, _selected);
        if (_selectedConnector is VerticalConnector sc)
            renderer.DrawLine(new(_geo.X(sc.Column), _geo.YRow(sc.TopRow)),
                              new(_geo.X(sc.Column), _geo.YRow(sc.BottomRow)),
                              new StrokeStyle(DrawingTheme.Blue, 0.5));

        // 縦コネクタ配置プレビュー（ドラッグ中の仮線）
        if (_connStartRow is int cs)
        {
            double x = _geo.X(_connBoundary);
            int top = Math.Min(cs, _connCurRow), bot = Math.Max(cs, _connCurRow);
            renderer.DrawLine(new(x, _geo.YRow(top)), new(x, _geo.YRow(bot)),
                new StrokeStyle(DrawingTheme.Blue, 0.3));
        }

        // 枠ドラッグ中のプレビュー（mm 連続座標）
        if (_frameStartMm is (double sx, double sy))
        {
            double fx = Math.Min(sx, _frameCurMm.X), fy = Math.Min(sy, _frameCurMm.Y);
            double fw = Math.Abs(_frameCurMm.X - sx), fh = Math.Abs(_frameCurMm.Y - sy);
            renderer.DrawRectangle(new(fx, fy, fw, fh), new StrokeStyle(DrawingTheme.Blue, 0.25, LineStyle.Dashed));
        }

        // 選択中の枠ハイライト
        if (_selectedFrame is GroupFrame sf)
        {
            double fx = sf.VisualXMm ?? _geo.X(sf.TopLeft.Column);
            double fy = sf.VisualYMm ?? (_geo.YRow(sf.TopLeft.Row) - _geo.CellMm * 0.4);
            double fw = sf.VisualWidthMm ?? sf.Width * _geo.CellMm;
            double fh = sf.VisualHeightMm ?? sf.Height * _geo.CellMm;
            renderer.DrawRectangle(new(fx, fy, fw, fh), new StrokeStyle(DrawingTheme.Blue, 0.4));
        }
    }

    private void DrawSelection(Win2DRenderer r, ElementInstance e)
    {
        var (l, right) = PartResolver.BoundarySpan(e, _document.Library);
        double pad = _geo.CellMm * 0.12;
        double x = _geo.X(l) - pad, y = _geo.YRow(e.Pos.Row) - _geo.CellMm * 0.5 - pad;
        double w = (right - l) * _geo.CellMm + 2 * pad, h = _geo.CellMm + 2 * pad;
        r.DrawRectangle(new(x, y, w, h), new StrokeStyle(DrawingTheme.Blue, 0.3));
    }

    private void DrawElementHighlight(Win2DRenderer r, ElementInstance e, Color color)
    {
        var (l, right) = PartResolver.BoundarySpan(e, _document.Library);
        double pad = _geo.CellMm * 0.1;
        double x = _geo.X(l) - pad, y = _geo.YRow(e.Pos.Row) - _geo.CellMm * 0.5 - pad;
        double w = (right - l) * _geo.CellMm + 2 * pad, h = _geo.CellMm + 2 * pad;
        r.FillRectangle(new(x, y, w, h), color);
    }

    // ===== ツール選択 =====

    // 縦パレットの接点系ラジオボタンの選択を解除（その他部品・フォルダ図形が配置対象であることを明示）。
    private void ClearToolRadios()
    {
        foreach (UIElement child in ToolStackPanel.Children)
            if (child is RadioButton rb) rb.IsChecked = false;
    }

    private void OnToolSelected(object sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioButton)?.Tag as string;
        _placeConnector = tag == "connector";
        _placeFrame = tag == "frame";
        _frameStartMm = null;
        _connStartRow = null;
        _placePartId = null;
        _placeKind = Enum.TryParse<ElementKind>(tag, out var k) ? k : null;
        if (OtherPartButton is not null) OtherPartButton.Content = "その他部品";
    }

    // 「その他部品」メニューから記号を選択（ツールバーに常設しない記号の配置）
    private void OnOtherPartSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;

        _placeConnector = false;
        _connStartRow = null;
        ClearToolRadios();

        if (tag.StartsWith("part:", StringComparison.Ordinal))
        {
            // 自作パーツ（Tag = "part:<PartId>"）。Kind は PartId 指定時に無視されるが既定値を置く。
            _placePartId = tag["part:".Length..];
            _placeKind = ElementKind.ContactNO;
        }
        else
        {
            if (!Enum.TryParse<ElementKind>(tag, out var kind)) return;
            _placePartId = null;
            _placeKind = kind;
        }
        OtherPartButton.Content = item.Text;
        Canvas.Invalidate();
    }

    // 「自作パーツ作成...」: パーツエディタ（別ウィンドウ）を開き、保存時にライブラリへ登録する。
    private void OnCreatePart(object sender, RoutedEventArgs e) => OpenPartEditor(null);

    private void OpenPartEditor(PartDefinition? edit)
    {
        var win = new PartEditorWindow(edit);
        win.Saved += def =>
        {
            _document.Library ??= new PartLibrary();
            _document.Library.ById[def.Id] = def;
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        };
        win.Activate();
    }

    // 既存の自作パーツをエディタで再編集する（Saved 時に同 Id で上書き）。
    private void OnEditPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string id && _document.Library?.Get(id) is { } def)
            OpenPartEditor(def);
    }

    // 自作パーツの削除（確認ダイアログ後にライブラリから除去）。配置済み要素は描画フォールバックされる。
    private async void OnDeletePart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || _document.Library?.Get(id) is not { } def) return;

        int placed = _document.Sheets.Sum(s => s.Elements.Count(el => el.PartId == id));
        string body = placed > 0
            ? $"パーツ「{def.Name}」を削除しますか？\n配置済みの {placed} 個の要素は組込み記号で表示されます。"
            : $"パーツ「{def.Name}」を削除しますか？";

        var dlg = new ContentDialog
        {
            Title = "パーツの削除",
            Content = body,
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _document.Library!.ById.Remove(id);
        if (_placePartId == id)
        {
            _placePartId = null;
            if (OtherPartButton is not null) OtherPartButton.Content = "その他部品";
        }
        RebuildOtherPartMenu();
        Canvas.Invalidate();
    }

    // パーツライブラリを外部ファイル（.gcadparts）へ書き出す。
    private async void OnExportParts(object sender, RoutedEventArgs e)
    {
        if (_document.Library is null || _document.Library.ById.Count == 0) return;

        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "parts";
        picker.FileTypeChoices.Add("パーツライブラリ", new List<string> { ".gcadparts" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try { PartLibrarySerializer.Save(_document.Library, file.Path); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // 外部ファイルからパーツを読み込み、現ドキュメントのライブラリへ統合（同 Id は上書き）。
    private async void OnImportParts(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".gcadparts");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var parts = PartLibrarySerializer.Load(file.Path);
            if (parts.Count == 0) { await ShowErrorAsync("読み込むパーツがありません。"); return; }

            _document.Library ??= new PartLibrary();
            foreach (var p in parts) _document.Library.ById[p.Id] = p;
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // 「その他部品」メニューを再生成（組込みの常設記号＋ドキュメントの自作パーツ）。
    private void RebuildOtherPartMenu()
    {
        if (OtherPartButton?.Flyout is not MenuFlyout flyout) return;
        flyout.Items.Clear();

        AddOtherBuiltins(flyout.Items);

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildCustomShapesSubItem());   // 図形/自作/ の自作図形（上メニューと共有）

        flyout.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "自作図形を作成..." };
        create.Click += OnCreateFolderPart;
        flyout.Items.Add(create);

        var import = new MenuFlyoutItem { Text = "パーツをインポート..." };
        import.Click += OnImportParts;
        flyout.Items.Add(import);

        var export = new MenuFlyoutItem { Text = "パーツをエクスポート..." };
        export.Click += OnExportParts;
        flyout.Items.Add(export);
    }

    // 自作パーツ1件分のサブメニュー（配置／編集／削除）。
    private MenuFlyoutSubItem BuildPartSubMenu(PartDefinition p)
    {
        var sub = new MenuFlyoutSubItem { Text = string.IsNullOrEmpty(p.Name) ? "(無名)" : p.Name };

        var place = new MenuFlyoutItem { Text = "配置", Tag = $"part:{p.Id}" };
        place.Click += OnOtherPartSelected;
        sub.Items.Add(place);

        var edit = new MenuFlyoutItem { Text = "編集...", Tag = p.Id };
        edit.Click += OnEditPart;
        sub.Items.Add(edit);

        var del = new MenuFlyoutItem { Text = "削除", Tag = p.Id };
        del.Click += OnDeletePart;
        sub.Items.Add(del);

        return sub;
    }

    // 「その他図形」の組込み記号（基本記号 a接点〜端子台 は含めない）。上メニューと左パネルで共有。
    private static readonly (string Label, string Tag)[] OtherBuiltins =
    {
        ("セレクトSW", "SelectSwitch"),
        ("サーマル(OL)", "ThermalOverload"),
        ("非常停止", "EmergencyStop"),
        ("三相モータ", "Motor"),
    };

    private void AddOtherBuiltins(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (label, tag) in OtherBuiltins)
        {
            var item = new MenuFlyoutItem { Text = label, Tag = tag };
            item.Click += OnOtherPartSelected;
            items.Add(item);
        }
    }

    // ===== テストモード =====

    private void OnTestModeToggle(object sender, RoutedEventArgs e)
    {
        _testMode = TestModeBtn.IsChecked == true;
        _testSessions.Clear();
        _testSession = _testMode ? GetOrCreateTestSession(_sheet) : null;
        // モード切替時に進行中のドラッグ/パン状態とキャプチャを破棄（戻ったとき選択/配置が効かなくなるのを防ぐ）。
        _testPressedDevice = null;
        ResetDragState();
        Canvas.ReleasePointerCaptures();
        TimerTickPanel.Visibility = _testMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateTestStatus();
        Canvas.Invalidate();
    }

    private void OnTimerTick(object sender, RoutedEventArgs e)
    {
        if (_testSession is null) return;
        double dt = double.IsNaN(TickValueBox.Value) ? 0.5 : Math.Max(0.01, TickValueBox.Value);
        _testSession.Tick(dt);
        UpdateTestStatus();
        Canvas.Invalidate();
    }

    /// <summary>シートに対応するテストセッションを取得（無ければ生成し状態を保持）。</summary>
    private TestSession GetOrCreateTestSession(Sheet sheet)
    {
        if (!_testSessions.TryGetValue(sheet, out var session))
        {
            session = new TestSession(sheet);
            session.Evaluate();
            _testSessions[sheet] = session;
        }
        return session;
    }

    private void UpdateTestStatus()
    {
        if (!_testMode)
        {
            StatusMode.Text = "作画モード";
            StatusWarn.Visibility = Visibility.Collapsed;
            UpdateStatusExtras();
            return;
        }

        StatusMode.Text = _testSession?.Result?.Status switch
        {
            EvalStatus.Diverging => "テストモード [発振エラー]",
            EvalStatus.Cyclic => $"テストモード [周期動作 T={_testSession.Result.CycleLength}]",
            _ => "テストモード",
        };

        var warnings = new List<string>();

        // P1: 短絡検出
        var shortNets = _testSession?.Result?.ShortCircuitNets;
        if (shortNets?.Count > 0)
            warnings.Add($"デッドショート検出 ({shortNets.Count} ネット)");

        // P3/P2/P6: DRC（テストモード中は毎更新時に評価）
        CircuitNumberer.Number(_document);
        var drcAll = DesignRuleCheck.CheckCrossReference(_document, _document.Library)
            .Concat(DesignRuleCheck.CheckDeviceTypeConsistency(_document, _document.Library))
            .ToList();
        // P7/P8: シート単位の接続検査・負荷到達可能性
        if (_sheet != null)
        {
            var sheetNet = NetlistBuilder.Build(_sheet, _document.Library);
            drcAll.AddRange(DesignRuleCheck.CheckVerticalCrossings(_sheet, sheetNet));
            drcAll.AddRange(DesignRuleCheck.CheckLoadReachability(_sheet, sheetNet));
        }
        int errCnt = drcAll.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnCnt = drcAll.Count(d => d.Severity == DiagnosticSeverity.Warning);
        if (errCnt > 0) warnings.Add($"DRC {errCnt} エラー");
        if (warnCnt > 0) warnings.Add($"DRC {warnCnt} 警告");

        if (warnings.Count > 0)
        {
            StatusWarn.Text = string.Join("  |  ", warnings);
            StatusWarn.Visibility = Visibility.Visible;
        }
        else
        {
            StatusWarn.Visibility = Visibility.Collapsed;
        }

        UpdateStatusExtras();
    }

    // Undo 履歴に乗らない変更（ドキュメント情報・シート設定・BOM 等）を確実にダーティ表示にする。
    // _savedUndoDepth=-1 は UndoDepth と決して一致しないセンチネル。次回保存でリセットされる。
    private void MarkDirty()
    {
        _savedUndoDepth = -1;
        UpdateStatusExtras();
    }

    private void UpdateStatusExtras()
    {
        int sheetIdx = _document.Sheets.IndexOf(_sheet) + 1;
        int total = _document.Sheets.Count;
        StatusSheet.Text = $"シート {sheetIdx} / {total}";

        bool dirty = _history.UndoDepth != _savedUndoDepth;
        StatusDirty.Text = "変更済み●";
        StatusDirty.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        // ウィンドウタイトルへファイル名・変更フラグを反映
        if (((App)Application.Current).MainWindow is MainWindow win)
            win.SetDocumentTitle(_currentPath, dirty);
    }

    // ===== 機器表パネル =====

    private void OnDevicePanelToggle(object sender, RoutedEventArgs e)
        => SetRightPanelVisible(!_devicePanelVisible);

    private void OnRightPanelToggle(object sender, RoutedEventArgs e)
        => OnDevicePanelToggle(sender, e);

    private const double RightPanelWidth = 220;

    /// <summary>右パネル（機器表／プロパティ）をスライド表示/折りたたみし、トグル表示も同期する。</summary>
    private void SetRightPanelVisible(bool visible)
    {
        _devicePanelVisible = visible;
        DevicePanelMenuItem.Text = visible ? "機器表を隠す" : "機器表を表示";
        if (RightPanelToggleGlyph != null) RightPanelToggleGlyph.Text = visible ? "▶" : "◀";
        if (visible)
        {
            RefreshDevicePanel();
            DevicePanel.Visibility = Visibility.Visible;
            AnimateSize(DevicePanel, isWidth: true, to: RightPanelWidth);
        }
        else
        {
            AnimateSize(DevicePanel, isWidth: true, to: 0,
                done: () => DevicePanel.Visibility = Visibility.Collapsed);
        }
    }

    /// <summary>FrameworkElement の Width/Height を滑らかにアニメーションする（スライド開閉用）。</summary>
    private static void AnimateSize(FrameworkElement el, bool isWidth, double to, Action? done = null)
    {
        double from = isWidth ? el.ActualWidth : el.ActualHeight;
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, isWidth ? "Width" : "Height");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (isWidth) el.Width = to; else el.Height = to;   // 終端値を確定
            done?.Invoke();
        };
        sb.Begin();
    }

    private void RefreshDevicePanel()
    {
        if (!_devicePanelVisible) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string Name, string Label, Sheet Sheet, ElementInstance Elem)>();

        foreach (var sheet in _document.Sheets)
        {
            foreach (var elem in sheet.Elements.OrderBy(e => e.Pos.Row).ThenBy(e => e.Pos.Column))
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;
                if (!seen.Add(elem.DeviceName)) continue;
                var kind = PartResolver.ComponentKind(elem, _document.Library);
                items.Add((elem.DeviceName, DeviceKindLabel(kind), sheet, elem));
            }
        }
        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        DeviceListView.Items.Clear();
        foreach (var (name, label, sheet, elem) in items)
        {
            DeviceListView.Items.Add(new ListViewItem
            {
                Content = $"[{label}]  {name}",
                Tag = (sheet, elem),
                Padding = new Thickness(8, 2, 8, 2),
            });
        }
    }

    // ===== プロパティパネル =====

    private void ShowPropertiesPanel()
    {
        if (!_devicePanelVisible) SetRightPanelVisible(true);
        RightPanelTabView.SelectedIndex = 1;
    }

    private bool _refreshingProps;

    private void RefreshPropertiesPanel()
    {
        _refreshingProps = true;
        PropertiesPanel.Children.Clear();

        if (_selected is null)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "要素を選択してください",
                FontSize = 11,
                Opacity = 0.6,
            });
            _refreshingProps = false;
            return;
        }

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = $"{DeviceKindLabel(_selected.Kind)}  {_selected.DeviceName ?? "—"}",
            FontSize = 12,
        });

        // 一般属性（全要素共通）: 機器名・コメント
        AddGeneralAttributes(_selected);

        // ラベル表示位置（基本パーツのみ。自作パーツは対象外）
        if (_selected.PartId is null)
            AddLabelPositionSelector(_selected);

        if (_selected.Kind == ElementKind.SelectSwitch)
        {
            int pos = _selected.Params.TryGetValue("Position", out var ps) &&
                      int.TryParse(ps, out int n) ? n : 0;
            PropertiesPanel.Children.Add(new TextBlock { Text = "ノッチ位置", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var nb = new NumberBox
            {
                Value = pos,
                Minimum = 0,
                Maximum = 99,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            nb.ValueChanged += OnPositionBoxChanged;
            PropertiesPanel.Children.Add(nb);
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "同名接点に異なる番号を設定し、テストモードでクリックして切り替えます",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        else if (_selected.Kind == ElementKind.Timer)
        {
            double setpoint = _selected.Params.TryGetValue("Setpoint", out var sp) &&
                double.TryParse(sp, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
            PropertiesPanel.Children.Add(new TextBlock { Text = "設定時間 (秒)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var nb = new NumberBox
            {
                Value = setpoint,
                Minimum = 0,
                Maximum = 9999,
                SmallChange = 0.1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            nb.ValueChanged += OnSetpointBoxChanged;
            PropertiesPanel.Children.Add(nb);
        }
        else if (_selected.Kind == ElementKind.Lamp)
        {
            var lampElem = _selected;
            string color = lampElem.Params.TryGetValue("LampColor", out var c) ? c : "";
            PropertiesPanel.Children.Add(new TextBlock { Text = "ランプ色（中央表示）", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
            var tb = new TextBox
            {
                Text = color,
                FontSize = 12,
                MaxLength = 4,
                PlaceholderText = "例: R / G / 赤",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            tb.LostFocus += (_, _) => CommitLampColor(lampElem, tb.Text);
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter) { CommitLampColor(lampElem, tb.Text); e.Handled = true; }
            };
            PropertiesPanel.Children.Add(tb);
        }

        // 要素を選択したら右パネルを表示しプロパティタブへ切替（プロパティ編集をすぐ可能に）
        ShowPropertiesPanel();

        _refreshingProps = false;
    }

    private void OnPositionBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshingProps || _selected is null || double.IsNaN(args.NewValue)) return;
        _history.Execute(new SetParamCommand(_sheet, _selected, "Position", ((int)args.NewValue).ToString()));
        Canvas.Invalidate();
    }

    private void OnSetpointBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshingProps || _selected is null || double.IsNaN(args.NewValue)) return;
        _history.Execute(new SetParamCommand(_sheet, _selected, "Setpoint",
            args.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Canvas.Invalidate();
    }

    private void CommitLampColor(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string val = text.Trim();
        string cur = elem.Params.TryGetValue("LampColor", out var c) ? c : "";
        if (val == cur) return;
        _history.Execute(new SetParamCommand(_sheet, elem, "LampColor", val));
        Canvas.Invalidate();
    }

    /// <summary>機器名・コメントの一般属性編集欄をプロパティパネルへ追加する（全要素共通）。</summary>
    private void AddGeneralAttributes(ElementInstance elem)
    {
        PropertiesPanel.Children.Add(new TextBlock { Text = "機器名", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var nameBox = new TextBox
        {
            Text = elem.DeviceName ?? string.Empty,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nameBox.LostFocus += (_, _) => CommitPropDeviceName(elem, nameBox.Text);
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { CommitPropDeviceName(elem, nameBox.Text); e.Handled = true; }
        };
        PropertiesPanel.Children.Add(nameBox);

        PropertiesPanel.Children.Add(new TextBlock { Text = "コメント", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var commentBox = new TextBox
        {
            Text = elem.Comment ?? string.Empty,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        commentBox.LostFocus += (_, _) => CommitPropComment(elem, commentBox.Text);
        commentBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { CommitPropComment(elem, commentBox.Text); e.Handled = true; }
        };
        PropertiesPanel.Children.Add(commentBox);
    }

    private void CommitPropDeviceName(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string newName = text.Trim();
        if (newName == (elem.DeviceName ?? string.Empty)) return;
        _history.Execute(new RenameDeviceCommand(_sheet, elem, elem.DeviceName,
            string.IsNullOrEmpty(newName) ? null : newName));
        RefreshDevicePanel();
        Canvas.Invalidate();
    }

    private void CommitPropComment(ElementInstance elem, string text)
    {
        if (_refreshingProps) return;
        string newComment = text.Trim();
        if (newComment == (elem.Comment ?? string.Empty)) return;
        _history.Execute(new SetCommentCommand(_sheet, elem, elem.Comment,
            string.IsNullOrEmpty(newComment) ? null : newComment));
        Canvas.Invalidate();
    }

    /// <summary>ラベル（機器名・コメント）の高さオフセット調整を追加する（正で上へ、密集時の重なり回避）。</summary>
    private void AddLabelPositionSelector(ElementInstance elem)
    {
        double dy = elem.Params.TryGetValue("LabelDy", out var v) &&
            double.TryParse(v, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double d)
            ? d : ElementCatalog.DefaultLabelDy(elem.Kind);   // 未設定は種別既定値を表示
        PropertiesPanel.Children.Add(new TextBlock { Text = "ラベル高さ調整 (mm・正で上)", FontSize = 11, Margin = new Thickness(0, 8, 0, 2) });
        var nb = new NumberBox
        {
            Value = dy,
            Minimum = -20,
            Maximum = 20,
            SmallChange = 0.5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nb.ValueChanged += OnLabelDyChanged;
        PropertiesPanel.Children.Add(nb);
    }

    private void OnLabelDyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshingProps || _selected is null || double.IsNaN(args.NewValue)) return;
        _history.Execute(new SetParamCommand(_sheet, _selected, "LabelDy",
            args.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Canvas.Invalidate();
    }

    private void OnDeviceListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceListView.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not (Sheet sheet, ElementInstance elem)) return;
        DeviceListView.SelectedItem = null;   // 選択をリセット（次回の同一機器クリックを有効にする）
        SwitchToSheet(sheet);
        SelectElement(elem);
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    private static string DeviceKindLabel(ElementKind kind) => kind switch
    {
        ElementKind.Coil or ElementKind.Timer => "コイル",
        ElementKind.ContactNO or ElementKind.ContactNC => "接点",
        ElementKind.PushButtonNO or ElementKind.PushButtonNC => "押釦",
        ElementKind.EmergencyStop => "非停",
        ElementKind.ThermalOverload => "OL",
        ElementKind.TimerContactNO or ElementKind.TimerContactNC => "タイマ",
        ElementKind.SelectSwitch => "SS",
        ElementKind.Lamp => "表示灯",
        ElementKind.Terminal => "端子",
        ElementKind.Motor => "モータ",
        _ => "?",
    };

    // ===== シート管理（複数シート切替） =====

    private void RebuildNavTree()
    {
        _suppressNavEvents = true;
        NavTree.RootNodes.Clear();
        _sheetNodeMap.Clear();
        for (int i = 0; i < _document.Sheets.Count; i++)
        {
            var sheet = _document.Sheets[i];
            var node = new TreeViewNode { Content = SheetDisplayName(sheet, i) };
            _sheetNodeMap[node] = sheet;
            NavTree.RootNodes.Add(node);
            if (sheet == _sheet) NavTree.SelectedNode = node;
        }
        _suppressNavEvents = false;
        UpdateStatusExtras();
    }

    private static string SheetDisplayName(Sheet sheet, int index)
        => string.IsNullOrWhiteSpace(sheet.Name) ? $"シート {index + 1}" : sheet.Name;

    /// <summary>再生成せずに指定シートのタブを選択状態にする（イベント抑制つき）。</summary>
    private void SelectNavTreeSheet(Sheet sheet)
    {
        _suppressNavEvents = true;
        foreach (var (node, s) in _sheetNodeMap)
            if (ReferenceEquals(s, sheet)) { NavTree.SelectedNode = node; break; }
        _suppressNavEvents = false;
    }

    private void OnNavTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_suppressNavEvents) return;
        if (NavTree.SelectedNode is TreeViewNode node && _sheetNodeMap.TryGetValue(node, out var sheet))
            SwitchToSheet(sheet);
    }

    private void SwitchToSheet(Sheet sheet)
    {
        if (ReferenceEquals(sheet, _sheet)) return;
        if (_editingElement != null) CommitDeviceName(accept: true);
        if (_editingComment != null) CommitComment(accept: true);
        if (_editingRungComment != null) CommitRungComment(accept: true);
        _sheet = sheet;
        _selected = null;
        _selectedConnector = null;
        _moving = null;
        RefreshPropertiesPanel();
        // テストセッションはシート単位。各シートの状態を保持して切り替える
        if (_testMode)
        {
            _testSession = GetOrCreateTestSession(_sheet);
            UpdateTestStatus();
        }
        SelectNavTreeSheet(_sheet);
        Canvas.Invalidate();
    }

    private void OnAddSheetBtn(object sender, RoutedEventArgs e)
    {
        CommitDeviceName(true);
        CommitComment(true);
        CommitRungComment(true);
        var def = _document.Settings.DefaultBus;
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = _sheet.Grid.Columns, Rows = 8 },
            Bus = new BusConfig { LeftName = def.LeftName, RightName = def.RightName, PowerLabel = def.PowerLabel },
        };
        _document.Sheets.Add(sheet);
        _sheet = sheet;
        ClearSelection();
        _moving = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        RebuildNavTree();
        RefreshPropertiesPanel();
        MarkDirty();
        Canvas.Invalidate();
    }

    private void OnDeleteSheetBtn(object sender, RoutedEventArgs e)
    {
        if (_document.Sheets.Count <= 1) return;
        CommitDeviceName(true);
        CommitComment(true);
        CommitRungComment(true);
        var sheet = _sheet;
        int idx = _document.Sheets.IndexOf(sheet);
        _document.Sheets.Remove(sheet);
        _history.RemoveCommandsForSheet(sheet);
        _testSessions.Remove(sheet);
        _sheet = _document.Sheets[Math.Clamp(idx, 0, _document.Sheets.Count - 1)];
        ClearSelection();
        _moving = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        _findResults.RemoveAll(r => ReferenceEquals(r.Sheet, sheet));
        if (_findIndex >= _findResults.Count) _findIndex = _findResults.Count - 1;
        RebuildNavTree();
        UpdateFindStatus();
        RefreshPropertiesPanel();
        MarkDirty();
        Canvas.Invalidate();
    }

    private async void OnRenameSheetBtn(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _sheet.Name, PlaceholderText = "シート名" };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            Title = "シート名の変更",
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _sheet.Name = box.Text.Trim();
            RebuildNavTree();
            MarkDirty();
        }
    }

    // ===== コピー・ペースト =====

    private void CopySelection()
    {
        var targets = _selectedSet.Count > 0
            ? _selectedSet.ToList()
            : _selected is not null ? new List<ElementInstance> { _selected } : new();
        if (targets.Count == 0) return;

        int minRow = targets.Min(e => e.Pos.Row);
        int minCol = targets.Min(e => e.Pos.Column);
        _clipboard = new ClipboardData(
            Elements: targets.Select(e =>
            {
                var clone = e.DeepClone();
                clone.Pos = new GridPos(e.Pos.Row - minRow, e.Pos.Column - minCol);
                return clone;
            }).ToList(),
            OriginRow: minRow,
            OriginCol: minCol);
    }

    private string? ResolveUniqueName(string? name)
    {
        if (name is null) return null;
        var existing = new HashSet<string>(
            _document.Sheets.SelectMany(s => s.Elements)
                .Select(e => e.DeviceName)
                .Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (int i = 2; i < 999; i++)
        {
            string candidate = $"{name}_{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return name;
    }

    private void PasteSelection()
    {
        if (_clipboard is null) return;
        int baseRow = _selected?.Pos.Row ?? 0;
        int baseCol = _selected?.Pos.Column ?? 0;

        var cmds = _clipboard.Elements
            .Select(e =>
            {
                var placed = e.DeepClone();
                placed.Pos = new GridPos(baseRow + e.Pos.Row, baseCol + e.Pos.Column);
                placed.DeviceName = ResolveUniqueName(placed.DeviceName);
                return (IUndoCommand)new PlaceElementCommand(_sheet, placed);
            })
            .ToList();

        _history.Execute(new BatchCommand(_sheet, cmds));
        RefreshDevicePanel();
        Canvas.Invalidate();
    }

    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { if (!_testMode) { CopySelection(); args.Handled = true; } }

    private void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { if (!_testMode) { PasteSelection(); args.Handled = true; } }

    // ===== 削除 =====

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selectedSet.Count > 0)
        {
            var cmds = _selectedSet
                .Select(e => (IUndoCommand)new DeleteElementCommand(_sheet, e))
                .ToList();
            _history.Execute(new BatchCommand(_sheet, cmds));
            _selectedSet.Clear();
            _selected = null;
            RefreshDevicePanel();
            Canvas.Invalidate();
            return;
        }
        if (_selected is not null)
        {
            _history.Execute(new DeleteElementCommand(_sheet, _selected));
            _selected = null;
            RefreshDevicePanel();
            Canvas.Invalidate();
        }
        else if (_selectedConnector is not null)
        {
            _history.Execute(new DeleteConnectorCommand(_sheet, _selectedConnector));
            _selectedConnector = null;
            Canvas.Invalidate();
        }
        else if (_selectedFrame is not null)
        {
            _history.Execute(new DeleteFrameCommand(_sheet, _selectedFrame));
            _selectedFrame = null;
            Canvas.Invalidate();
        }
    }

    private GroupFrame? HitTestFrame(double xMm, double yMm)
    {
        double margin = Math.Min(_geo.CellMm * 0.15, 3.0);
        var hits = new List<GroupFrame>();
        foreach (var f in _sheet.Frames)
        {
            double fx = f.VisualXMm ?? _geo.X(f.TopLeft.Column);
            double fy = f.VisualYMm ?? (_geo.YRow(f.TopLeft.Row) - _geo.CellMm * 0.4);
            double fw = f.VisualWidthMm ?? f.Width * _geo.CellMm;
            double fh = f.VisualHeightMm ?? f.Height * _geo.CellMm;
            bool insideX = xMm >= fx - margin && xMm <= fx + fw + margin;
            bool insideY = yMm >= fy - margin && yMm <= fy + fh + margin;
            bool onBorderX = xMm <= fx + margin || xMm >= fx + fw - margin;
            bool onBorderY = yMm <= fy + margin || yMm >= fy + fh - margin;
            if (insideX && insideY && (onBorderX || onBorderY))
                hits.Add(f);
        }
        return hits.Count == 0 ? null : hits.MinBy(f => f.Width * f.Height);
    }

    // ===== キーボード =====

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
                     CoreVirtualKeyStates.Down) != 0;

        // Ctrl+Z/Y は KeyboardAccelerator 側で処理。ここでは Ctrl+F のみ。
        if (ctrl && e.Key == VirtualKey.F) { ToggleFindBar(); e.Handled = true; return; }

        switch (e.Key)
        {
            case VirtualKey.Back:
                DeleteSelected(); e.Handled = true; break;

            case VirtualKey.F5: ActivateTool("ContactNO"); e.Handled = true; break;
            case VirtualKey.F6: ActivateTool("ContactNC"); e.Handled = true; break;
            case VirtualKey.F7: ActivateTool("Coil"); e.Handled = true; break;
            case VirtualKey.F8: ActivateTool("PushButtonNO"); e.Handled = true; break;

            case VirtualKey.Escape:
                if (_rangeSelecting) { _rangeSelecting = false; _selectedSet.Clear(); Canvas.Invalidate(); e.Handled = true; break; }
                if (_editingElement != null) CommitDeviceName(accept: false);
                else if (_editingComment != null) CommitComment(accept: false);
                else if (_editingRungComment != null) CommitRungComment(accept: false);
                else if (_editingFrame != null) CommitFrameLabel(accept: false);
                else if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
                else if (_placeKind != null || _placeConnector || _placeFrame) ActivateTool("select");
                e.Handled = true;
                break;

            case VirtualKey.Space:
                if (!_testMode && _editingElement is null && _editingComment is null
                    && _editingRungComment is null && _editingFrame is null
                    && FindBar.Visibility != Visibility.Visible)
                {
                    _spacePanActive = true;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space)
        {
            _spacePanActive = false;
            e.Handled = true;
        }
    }

    // ===== 検索・置換 =====

    private void OnFindBoxTextChanged(object sender, TextChangedEventArgs e) => UpdateFindResults();

    private void UpdateFindResults()
    {
        string query = FindBox.Text.Trim();
        _findResults = string.IsNullOrEmpty(query)
            ? new()
            : _document.Sheets
                .SelectMany(sh => sh.Elements
                    .Where(el => string.Equals(el.DeviceName, query, StringComparison.OrdinalIgnoreCase))
                    .Select(el => (Sheet: sh, El: el)))
                .ToList();
        _findIndex = _findResults.Count > 0 ? 0 : -1;
        if (_findIndex >= 0) JumpToFindResult();   // 最初の一致へ（別シートなら切替）
        UpdateFindStatus();
        RefreshSearchResultPanel();
        Canvas.Invalidate();
    }

    /// <summary>現在の検索インデックスの一致要素へジャンプ（別シートなら切替）。</summary>
    private void JumpToFindResult()
    {
        if (_findIndex < 0 || _findIndex >= _findResults.Count) return;
        var hit = _findResults[_findIndex];
        // シート切替は SwitchToSheet 一本化（編集中コミット・選択/テストセッション整合を担保）。
        if (hit.Sheet != _sheet) SwitchToSheet(hit.Sheet);
        Canvas.Invalidate();
    }

    private void UpdateFindStatus()
    {
        if (_findResults.Count == 0)
            FindStatus.Text = FindBox.Text.Length > 0 ? "見つかりません" : string.Empty;
        else
            FindStatus.Text = $"{_findIndex + 1} / {_findResults.Count}";
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { OnFindNext(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (_findResults.Count == 0) return;
        _findIndex = (_findIndex + 1) % _findResults.Count;
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnFindPrev(object sender, RoutedEventArgs e)
    {
        if (_findResults.Count == 0) return;
        _findIndex = (_findIndex - 1 + _findResults.Count) % _findResults.Count;
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        if (_findIndex < 0 || _findIndex >= _findResults.Count) return;
        string newName = ReplaceBox.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        var (sheet, elem) = _findResults[_findIndex];
        _history.Execute(new RenameDeviceCommand(sheet, elem, elem.DeviceName, newName));
        UpdateFindResults();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        string query = FindBox.Text.Trim();
        string newName = ReplaceBox.Text.Trim();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(newName)) return;

        var targets = _document.Sheets
            .SelectMany(sh => sh.Elements.Select(el => (Sheet: sh, El: el)))
            .Where(t => string.Equals(t.El.DeviceName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (sheet, elem) in targets)
            _history.Execute(new RenameDeviceCommand(sheet, elem, elem.DeviceName, newName));
        UpdateFindResults();
    }

    private void OnFindClose(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findResults.Clear();
        _findIndex = -1;
        Canvas.Invalidate();
    }

    private void ToggleFindBar()
    {
        if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
        else { FindBar.Visibility = Visibility.Visible; FindBox.Focus(FocusState.Programmatic); }
    }

    // ===== 下部出力パネル (P3) =====

    private void OnRunDrc(object sender, RoutedEventArgs e)
    {
        CircuitNumberer.Number(_document);
        _lastDrcResults = DesignRuleCheck.CheckCrossReference(_document, _document.Library)
            .Concat(DesignRuleCheck.CheckDeviceTypeConsistency(_document, _document.Library))
            .ToList();

        int errCnt = _lastDrcResults.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnCnt = _lastDrcResults.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (_lastDrcResults.Count == 0)
        {
            DrcListView.ItemsSource = new List<string> { "問題なし（全シート）" };
            DrcStatusText.Text = "OK";
        }
        else
        {
            DrcListView.ItemsSource = _lastDrcResults.Select(FormatDiagnostic).ToList();
            DrcStatusText.Text = $"エラー {errCnt}  警告 {warnCnt}";
        }

        // 展開してDRCタブを選択
        if (_outputPanelCollapsed) ExpandOutputPanel();
        OutputTabView.SelectedIndex = 0;
    }

    private void OnDrcItemSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = DrcListView.SelectedIndex;
        if (idx < 0 || idx >= _lastDrcResults.Count) return;
        var diag = _lastDrcResults[idx];
        if (diag.Locations.Count == 0) return;
        var loc = diag.Locations[0];
        var target = _document.Sheets.FirstOrDefault(s => s.PageNumber == loc.PageNumber);
        // シート切替は SwitchToSheet 一本化（編集中コミット・選択/テストセッション整合を担保）。
        if (target != null && target != _sheet) SwitchToSheet(target);
    }

    private void RefreshSearchResultPanel()
    {
        if (_findResults.Count == 0)
        {
            SearchResultPanelView.ItemsSource = null;
            return;
        }
        SearchResultPanelView.ItemsSource = _findResults.Select((r, i) =>
        {
            string sheetName = string.IsNullOrWhiteSpace(r.Sheet.Name)
                ? $"P{r.Sheet.PageNumber}"
                : r.Sheet.Name;
            return $"{sheetName}: {r.El.DeviceName ?? "(無名)"} (行{r.El.Pos.Row + 1}, 列{r.El.Pos.Column + 1})";
        }).ToList();
    }

    private void OnSearchResultItemSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = SearchResultPanelView.SelectedIndex;
        if (idx < 0 || idx >= _findResults.Count) return;
        _findIndex = idx;
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnRunConnectivity(object sender, RoutedEventArgs e)
    {
        CircuitNumberer.Number(_document);
        var sheetNet = NetlistBuilder.Build(_sheet, _document.Library);
        var issues = DesignRuleCheck.CheckVerticalCrossings(_sheet, sheetNet)
            .Concat(DesignRuleCheck.CheckLoadReachability(_sheet, sheetNet))
            .ToList();

        ConnectivityListView.ItemsSource = issues.Count == 0
            ? (object)new List<string> { $"問題なし（{SheetDisplayName(_sheet, _document.Sheets.IndexOf(_sheet))}）" }
            : issues.Select(FormatDiagnostic).ToList();

        if (_outputPanelCollapsed) ExpandOutputPanel();
        OutputTabView.SelectedIndex = 2;
    }

    private void OnOutputPanelToggle(object sender, RoutedEventArgs e)
    {
        if (_outputPanelCollapsed)
            ExpandOutputPanel();
        else
            CollapseOutputPanel();
    }

    // 折りたたみ時もタブ列（右端の▼ボタン）を残すための高さ。
    private const double OutputPanelCollapsedHeight = 40;

    private void CollapseOutputPanel()
    {
        _outputPanelCollapsed = true;
        AnimateSize(OutputPanel, isWidth: false, to: OutputPanelCollapsedHeight);
        OutputCollapseBtn.Content = "▲";
    }

    private void ExpandOutputPanel()
    {
        _outputPanelCollapsed = false;
        AnimateSize(OutputPanel, isWidth: false, to: OutputPanelDefaultHeight);
        OutputCollapseBtn.Content = "▼";
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        string sev = d.Severity switch
        {
            DiagnosticSeverity.Error => "E",
            DiagnosticSeverity.Warning => "W",
            _ => "I"
        };
        string loc = d.Locations.Count > 0
            ? $" [P{d.Locations[0].PageNumber} 回路{d.Locations[0].CircuitNumber}]"
            : "";
        return $"[{sev}] {d.Code}{loc}  {d.Message}";
    }

    // ===== ヘルパー =====

    private bool CellEmpty(int row, int col, ElementInstance? except)
    {
        foreach (var e in _sheet.Elements)
        {
            if (e == except || e.Pos.Row != row) continue;
            var (l, r) = PartResolver.BoundarySpan(e, _document.Library);
            if (col >= e.Pos.Column && col < r) return false;
        }
        return true;
    }

    private ElementInstance? HitTest(int row, int col)
    {
        foreach (var e in _sheet.Elements)
        {
            if (e.Pos.Row != row) continue;
            var (l, r) = PartResolver.BoundarySpan(e, _document.Library);
            if (col >= e.Pos.Column && col < r) return e;
        }
        return null;
    }

    // 縦コネクタのヒット判定（線の近傍をクリック）
    private VerticalConnector? HitTestConnector(double xMm, double yMm)
    {
        double tol = _geo.CellMm * 0.25;
        foreach (var c in _sheet.Connectors)
        {
            double cx = _geo.X(c.Column);
            double yTop = _geo.YRow(c.TopRow), yBot = _geo.YRow(c.BottomRow);
            if (Math.Abs(xMm - cx) <= tol && yMm >= yTop - tol && yMm <= yBot + tol)
                return c;
        }
        return null;
    }

    private (double X, double Y) ToWorld(Point dip)
    {
        double scale = DipsPerMm * _zoom;
        return ((dip.X - _panX) / scale, (dip.Y - _panY) / scale);
    }

    private void CycleSelectSwitch(ElementInstance elem)
    {
        if (elem.DeviceName is null || _testSession is null) return;
        var positions = _sheet.Elements
            .Where(e => e.DeviceName == elem.DeviceName && e.Kind == ElementKind.SelectSwitch
                     && e.Params.TryGetValue("Position", out _))
            .Select(e => int.TryParse(e.Params["Position"], out int n) ? n : 0)
            .Distinct().OrderBy(x => x).ToList();
        if (positions.Count == 0) { _testSession.ToggleInput(elem.DeviceName); return; }
        int current = _testSession.State.Positions.TryGetValue(elem.DeviceName, out var pos) ? pos : 0;
        int idx = positions.IndexOf(current);
        _testSession.SetPosition(elem.DeviceName, positions[(idx + 1) % positions.Count]);
    }

    // %TEMP% は常に書き込み可。サンドボックス制限を受けない。
    private static readonly string _logFile = Path.Combine(Path.GetTempPath(), "guiecad_debug.log");
    private static void AppLog(string msg)
    {
        try { File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[AppLog FAIL] {ex.Message}"); }
    }

    private void ShowDrawingContextMenu(Point pos)
    {
        if (_testMode || _menuShowing) return;
        _menuShowing = true;
        var (xMm, yMm) = ToWorld(pos);
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
        var menu = new MenuFlyout();

        if (HitTest(row, col) is ElementInstance hitElem)
        {
            AddItem(menu, "削除(Del)", () =>
            {
                _history.Execute(new DeleteElementCommand(_sheet, hitElem));
                _selected = null;
                RefreshDevicePanel();
                Canvas.Invalidate();
            });
            AddItem(menu, "コメント編集(F2)", () => { SelectElement(hitElem); ShowCommentEditor(hitElem); });
            AddItem(menu, "機器名変更(Enter)", () => { SelectElement(hitElem); ShowDeviceNameEditor(hitElem); });
        }
        else if (HitTestConnector(xMm, yMm) is VerticalConnector hitVc)
        {
            AddItem(menu, "縦コネクタ削除", () =>
            {
                _history.Execute(new DeleteConnectorCommand(_sheet, hitVc));
                _selectedConnector = null;
                Canvas.Invalidate();
            });
        }
        else if (HitTestFrame(xMm, yMm) is GroupFrame hitFrame)
        {
            AddItem(menu, "ラベル編集", () => ShowFrameLabelEditor(hitFrame, pos));
            AddItem(menu, "削除", () =>
            {
                _history.Execute(new DeleteFrameCommand(_sheet, hitFrame));
                _selectedFrame = null;
                Canvas.Invalidate();
            });
            menu.Items.Add(new MenuFlyoutSeparator());
            var styleSub = new MenuFlyoutSubItem { Text = "線種" };
            foreach (var style in new[] { LineStyle.Solid, LineStyle.Dashed, LineStyle.Dotted })
            {
                var s = style;
                string label = s switch
                {
                    LineStyle.Solid => "実線",
                    LineStyle.Dashed => "破線（既定）",
                    LineStyle.Dotted => "点線",
                    _ => s.ToString(),
                };
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) =>
                {
                    _history.Execute(new SetFrameBorderStyleCommand(_sheet, hitFrame, hitFrame.BorderStyle, s));
                    Canvas.Invalidate();
                };
                styleSub.Items.Add(item);
            }
            menu.Items.Add(styleSub);
        }
        else if (row >= 0 && row < _sheet.Grid.Rows)
        {
            AddItem(menu, $"行 {row + 1} の前に行を挿入", () =>
            {
                _history.Execute(new InsertRowCommand(_sheet, row));
                Canvas.Invalidate();
            });
            AddItem(menu, "末尾に行を追加", () =>
            {
                _history.Execute(new InsertLastRowCommand(_sheet));
                Canvas.Invalidate();
            });
            if (_sheet.Grid.Rows > 1)
                AddItem(menu, $"行 {row + 1} を削除", () =>
                {
                    _history.Execute(new DeleteRowCommand(_sheet, row));
                    Canvas.Invalidate();
                });
        }

        if (menu.Items.Count > 0)
        {
            menu.Closed += (_, _) => _menuShowing = false;
            menu.ShowAt(Canvas, pos);
        }
        else
        {
            _menuShowing = false;
        }
    }

    private static void AddItem(MenuFlyout m, string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        m.Items.Add(item);
    }

    // 接点右クリックメニュー本体（WndProc フックと ContextRequested の両方から呼ばれる）
    private void ShowContactContextMenu(Point pos)
    {
        if (!_testMode || _testSession is null) return;

        var (xMm, yMm) = ToWorld(pos);
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
        var hit = HitTest(row, col);

        if (hit is null || hit.DeviceName is null) return;
        if (hit.Kind is not (ElementKind.ContactNO or ElementKind.ContactNC)) return;

        string dev = hit.DeviceName;
        bool isForced = _testSession.State.Inputs.TryGetValue(dev, out var cur) && cur;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = $"接点：{dev}  [{(isForced ? "手動ON中" : "シミュレーション依存")}]",
            IsEnabled = false,
        });
        menu.Items.Add(new MenuFlyoutSeparator());

        var forceOnItem = new MenuFlyoutItem { Text = "手動でON（強制閉路）", IsEnabled = !isForced };
        forceOnItem.Click += (_, _) =>
        {
            _testSession.SetInput(dev, true);
            UpdateTestStatus();
            Canvas.Invalidate();
        };
        menu.Items.Add(forceOnItem);

        var releaseItem = new MenuFlyoutItem { Text = "手動を解除（シミュレーション依存に戻す）", IsEnabled = isForced };
        releaseItem.Click += (_, _) =>
        {
            _testSession.SetInput(dev, false);
            UpdateTestStatus();
            Canvas.Invalidate();
        };
        menu.Items.Add(releaseItem);

        menu.ShowAt(Canvas, pos);
    }

    // 右クリック — 作画/テスト両モードのメインルート（CanvasControl は UIElement なので RightTapped で発火）
    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(Canvas);
        e.Handled = true;
        if (_testMode && _testSession is not null) ShowContactContextMenu(pos);
        else if (!_testMode) ShowDrawingContextMenu(pos);
    }

    // ===== ズーム/パン =====

    private void OnConnectivityToggle(object sender, RoutedEventArgs e)
    {
        _connectivityCheck = ConnectivityToggle.IsChecked == true;
        Canvas.Invalidate();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.2, ViewCenter());
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.2, ViewCenter());

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _zoom = 1.6; _panX = 20; _panY = 20;
        Canvas.Invalidate();
    }

    private const int MaxColumns = 20;

    private void OnAddColumn(object sender, RoutedEventArgs e)
    {
        if (_sheet.Grid.Columns >= MaxColumns) return;
        _sheet.Grid.Columns++;
        MarkDirty();
        Canvas.Invalidate();
    }

    private void OnRemoveColumn(object sender, RoutedEventArgs e)
    {
        // 要素がはみ出さない範囲でのみ縮小（最右要素の右境界より小さくはしない）
        int maxRight = 1;
        foreach (var el in _sheet.Elements)
        {
            var (_, right) = PartResolver.BoundarySpan(el, null);
            maxRight = Math.Max(maxRight, right);
        }
        if (_sheet.Grid.Columns > maxRight)
        {
            _sheet.Grid.Columns--;
            MarkDirty();
            Canvas.Invalidate();
        }
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas);
        double factor = Math.Pow(1.1, p.Properties.MouseWheelDelta / 120.0);
        ZoomBy(factor, p.Position);
    }

    private Point ViewCenter() => new(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2);

    private void ZoomBy(double factor, Point at)
    {
        double oldScale = DipsPerMm * _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 12.0);
        double newScale = DipsPerMm * _zoom;
        _panX = at.X - (at.X - _panX) * (newScale / oldScale);
        _panY = at.Y - (at.Y - _panY) * (newScale / oldScale);
        Canvas.Invalidate();
    }

    // ===== サンプルシート =====

    private static Sheet BuildSampleSheet()
    {
        ElementInstance El(ElementKind k, int row, int col, string? dev = null, int w = 1)
            => new() { Kind = k, Pos = new GridPos(row, col), CellWidth = w, DeviceName = dev };

        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = 8, Rows = 8 },
            Bus = new BusConfig { LeftName = "R200", RightName = "S200" },
        };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 7, "CR1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR1"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });
        sheet.Elements.Add(El(ElementKind.ContactNO, 3, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Lamp, 3, 7, "PL"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 5, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Terminal, 5, 3, "TB1"));
        sheet.Elements.Add(El(ElementKind.Coil, 5, 5, "SOL"));
        sheet.Elements.Add(El(ElementKind.Terminal, 5, 7, "TB2"));
        return sheet;
    }
}
