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
        // ツールバーの接点系ラジオボタンの選択を解除（その他部品が配置対象であることを明示）
        foreach (UIElement child in ToolStackPanel.Children)
            if (child is RadioButton rb) rb.IsChecked = false;

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

    // ===== 図形フォルダ（メニューバー「図形(G)」） =====

    // 起動時: フォルダ作成・基本図形シード・走査・メニュー構築（失敗してもアプリは起動継続）。
    private void InitShapeFolder()
    {
        _folderStore = PartFolderStore.CreateDefault();
        try
        {
            _folderStore.EnsureFolders();
            // 基本図形の実フォルダへのシードは廃止（配置は自作図形のみ・たたき台はコードの組込み図形から）
        }
        catch { /* フォルダ作成不可でもメニューだけは出す */ }

        _shapeMenuItem = ShapeMenuBarItem;   // XAML のプレースホルダを起点に差し替えていく
        RefreshFolderEntries();
        RebuildShapeMenu();
    }

    private void RefreshFolderEntries()
    {
        try { _folderEntries = _folderStore.Enumerate(); }
        catch { _folderEntries = Array.Empty<PartFolderEntry>(); }
    }

    // フォルダ走査結果を「図形」メニューへフォルダ階層（基本図形=直下 / 自作=サブメニュー）で展開する。
    // MenuBarItem は Items を Clear/Add しても表示が更新されないことがあるため、毎回 MenuBarItem ごと差し替える。
    private void RebuildShapeMenu()
    {
        var menu = new MenuBarItem { Title = "図形(G)" };

        _folderPartMap.Clear();
        foreach (var e in _folderEntries) _folderPartMap[e.Definition.Id] = e;

        // その他図形 = 組込みのその他記号 ＋ 自作図形（左パネル「その他▼」と同じ並び）
        var other = new MenuFlyoutSubItem { Text = "その他図形" };
        AddOtherBuiltins(other.Items);
        other.Items.Add(new MenuFlyoutSeparator());
        other.Items.Add(BuildCustomShapesSubItem());
        menu.Items.Add(other);

        menu.Items.Add(new MenuFlyoutSeparator());

        var create = new MenuFlyoutItem { Text = "自作図形を作成..." };
        create.Click += OnCreateFolderPart;
        menu.Items.Add(create);

        var open = new MenuFlyoutItem { Text = "図形フォルダを開く" };
        open.Click += OnOpenShapeFolder;
        menu.Items.Add(open);

        // 同じ位置（表示(V)とヘルプ(H)の間）へ差し替える
        int idx = TopMenuBar.Items.IndexOf(_shapeMenuItem);
        if (idx >= 0)
        {
            TopMenuBar.Items.RemoveAt(idx);
            TopMenuBar.Items.Insert(idx, menu);
        }
        else
        {
            TopMenuBar.Items.Add(menu);
        }
        _shapeMenuItem = menu;
    }

    // 「自作図形」サブメニュー（図形/自作/ 配下を配置/編集/削除）。上メニューと左パネル「その他▼」で共有。
    private MenuFlyoutSubItem BuildCustomShapesSubItem()
    {
        var sub = new MenuFlyoutSubItem { Text = "自作図形" };
        var customs = _folderEntries
            .Where(e => e.Category == "自作" || e.Category.StartsWith("自作/", StringComparison.Ordinal))
            .OrderBy(e => e.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (customs.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "(なし)", IsEnabled = false });
            return sub;
        }
        foreach (var e in customs) sub.Items.Add(BuildShapeSubMenu(e));
        return sub;
    }

    // 自作図形1件のサブメニュー（配置／編集／削除）。
    private MenuFlyoutSubItem BuildShapeSubMenu(PartFolderEntry e)
    {
        var def = e.Definition;
        var sub = new MenuFlyoutSubItem { Text = string.IsNullOrEmpty(def.Name) ? "(無名)" : def.Name };

        var place = new MenuFlyoutItem { Text = "配置", Tag = def.Id };
        place.Click += OnPlaceFolderPart;
        sub.Items.Add(place);

        var edit = new MenuFlyoutItem { Text = "編集...", Tag = def.Id };
        edit.Click += OnEditFolderPart;
        sub.Items.Add(edit);

        var del = new MenuFlyoutItem { Text = "削除", Tag = def.Id };
        del.Click += OnDeleteFolderPart;
        sub.Items.Add(del);

        return sub;
    }

    // フォルダの図形を配置対象にする。ドキュメント Library へ埋め込み（.GCAD 自己完結）を維持する。
    private void OnPlaceFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || !_folderPartMap.TryGetValue(id, out var entry)) return;

        _document.Library ??= new PartLibrary();
        _document.Library.ById[entry.Definition.Id] = entry.Definition;   // 埋め込み維持

        _placeConnector = false;
        _connStartRow = null;
        foreach (UIElement child in ToolStackPanel.Children)
            if (child is RadioButton rb) rb.IsChecked = false;

        _placePartId = entry.Definition.Id;
        _placeKind = ElementKind.ContactNO;   // PartId 指定時は無視されるが既定値を置く
        Canvas.Invalidate();
    }

    private void OnEditFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string id && _folderPartMap.TryGetValue(id, out var entry))
            OpenFolderPartEditor(entry.Definition, oldPath: entry.FilePath);
    }

    private void OnCreateFolderPart(object sender, RoutedEventArgs e) => OpenFolderPartEditor(null, oldPath: null);

    // 図形フォルダ向けのパーツエディタを開く。保存で「図形/自作」へ書き出し＋埋め込み＋メニュー再構成。
    private void OpenFolderPartEditor(PartDefinition? edit, string? oldPath)
    {
        var win = new PartEditorWindow(edit);
        win.Saved += def =>
        {
            try
            {
                string newPath = _folderStore.SaveCustom(def);
                // 改名で別ファイル名になった場合は旧ファイルを除去（自作の上書き編集を保つ）
                if (oldPath is not null &&
                    !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    _folderStore.Delete(oldPath);
            }
            catch (Exception ex) { _ = ShowErrorAsync(ex.Message); }

            _document.Library ??= new PartLibrary();
            _document.Library.ById[def.Id] = def;   // 埋め込みも更新
            RefreshFolderEntries();
            RebuildShapeMenu();
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        };
        win.Activate();
    }

    private async void OnDeleteFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || !_folderPartMap.TryGetValue(id, out var entry)) return;

        var dlg = new ContentDialog
        {
            Title = "図形の削除",
            Content = $"自作図形「{entry.Definition.Name}」をフォルダから削除しますか？",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try { _folderStore.Delete(entry.FilePath); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); return; }

        RefreshFolderEntries();
        RebuildShapeMenu();
        RebuildOtherPartMenu();
    }

    // 図形フォルダ（図形/）をエクスプローラーで開く。
    private async void OnOpenShapeFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            _folderStore.EnsureFolders();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _folderStore.RootDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // 基本図形を元に、新 Id の自作図形の下書きを作る（名前はエディタで改名する想定）。
    private void ActivateTool(string tag)
    {
        ResetDragState();
        _selectedSet.Clear();
        _placeConnector = tag == "connector";
        _placeFrame = tag == "frame";
        _frameStartMm = null;
        _connStartRow = null;
        if (tag == "select")
        {
            _placeKind = null;
            BtnSelect.IsChecked = true;
        }
        else if (Enum.TryParse<ElementKind>(tag, out var kind))
        {
            _placeKind = kind;
            foreach (UIElement child in ToolStackPanel.Children)
                if (child is RadioButton rb && rb.Tag?.ToString() == tag)
                { rb.IsChecked = true; break; }
        }
        else
        {
            _placeKind = null;
        }
        Canvas.Invalidate();
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

        // P3/P2/P6: DRC（テストモード開始時のみ評価=ドキュメント変更なし）
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
        => SetRightPanelVisible(!_devicePanelVisible);

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
        _selected = elem;
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
        var def = _document.Settings.DefaultBus;
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = _sheet.Grid.Columns, Rows = 8 },
            Bus = new BusConfig { LeftName = def.LeftName, RightName = def.RightName, PowerLabel = def.PowerLabel },
        };
        _document.Sheets.Add(sheet);
        _sheet = sheet;
        _selected = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        RebuildNavTree();
        Canvas.Invalidate();
    }

    private void OnDeleteSheetBtn(object sender, RoutedEventArgs e)
    {
        if (_document.Sheets.Count <= 1) return;
        var sheet = _sheet;
        int idx = _document.Sheets.IndexOf(sheet);
        _document.Sheets.Remove(sheet);
        _history.RemoveCommandsForSheet(sheet);
        _testSessions.Remove(sheet);
        _sheet = _document.Sheets[Math.Clamp(idx, 0, _document.Sheets.Count - 1)];
        _selected = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        _findResults.RemoveAll(r => ReferenceEquals(r.Sheet, sheet));
        if (_findIndex >= _findResults.Count) _findIndex = _findResults.Count - 1;
        RebuildNavTree();
        UpdateFindStatus();
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
        }
    }

    // ===== ドキュメント情報（表題欄） =====

    private async void OnDocumentInfo(object sender, RoutedEventArgs e)
    {
        var info = _document.Info;
        var fields = new (string Header, string Value, Action<string> Set)[]
        {
            ("図面名称", info.Title,    v => info.Title    = v),
            ("図番",     info.DrawingNo, v => info.DrawingNo = v),
            ("顧客",     info.Customer,  v => info.Customer  = v),
            ("設計",     info.Designer,  v => info.Designer  = v),
            ("製図",     info.Drafter,   v => info.Drafter   = v),
            ("確認",     info.Checker,   v => info.Checker   = v),
            ("日付",     info.Date ?? "", v => info.Date = string.IsNullOrEmpty(v) ? null : v),
        };

        var panel = new StackPanel { Spacing = 8 };
        var boxes = fields.Select(f => new TextBox { Text = f.Value, Header = f.Header }).ToArray();
        foreach (var box in boxes) panel.Children.Add(box);

        // 図面枠 ON/OFF トグル
        var borderToggle = new ToggleSwitch
        {
            Header = "図面枠を描画（A4横・PDF出力時）",
            IsOn = _document.Settings.EnableBorder,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(borderToggle);

        // 改定欄セクション
        panel.Children.Add(new TextBlock
        {
            Text = "改定履歴",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 12, 0, 0),
        });

        // 改定エントリのUI（動的に追加・削除）
        var revPanel = new StackPanel { Spacing = 4 };
        var revEntries = new List<(RevisionEntry Entry, TextBox[] Boxes)>();

        void AddRevRow(RevisionEntry rev)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var bRev  = new TextBox { Text = rev.Rev,         Width = 50,  PlaceholderText = "Rev" };
            var bDate = new TextBox { Text = rev.Date,        Width = 90,  PlaceholderText = "日付" };
            var bDesc = new TextBox { Text = rev.Description, Width = 200, PlaceholderText = "内容" };
            var bBy   = new TextBox { Text = rev.By,          Width = 70,  PlaceholderText = "担当" };
            var del   = new Button  { Content = "削除", Padding = new Thickness(8, 0, 8, 0) };
            del.Click += (_, _) => { revPanel.Children.Remove(rowPanel); revEntries.RemoveAll(x => x.Boxes[0] == bRev); };
            rowPanel.Children.Add(bRev); rowPanel.Children.Add(bDate);
            rowPanel.Children.Add(bDesc); rowPanel.Children.Add(bBy); rowPanel.Children.Add(del);
            revPanel.Children.Add(rowPanel);
            revEntries.Add((rev, new[] { bRev, bDate, bDesc, bBy }));
        }

        // 既存エントリを表示
        foreach (var rev in info.Revisions) AddRevRow(rev);

        var addRevBtn = new Button { Content = "+ 改定を追加", Margin = new Thickness(0, 4, 0, 0) };
        addRevBtn.Click += (_, _) => AddRevRow(new RevisionEntry());

        panel.Children.Add(revPanel);
        panel.Children.Add(addRevBtn);

        var dialog = new ContentDialog
        {
            Title = "ドキュメント情報",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        boxes[0].Loaded += (_, _) => boxes[0].Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        for (int i = 0; i < fields.Length; i++)
            fields[i].Set(boxes[i].Text.Trim());

        // 図面枠設定を保存
        _document.Settings.EnableBorder = borderToggle.IsOn;

        // 改定履歴を保存（現在のUIの並び順で上書き）
        info.Revisions.Clear();
        foreach (var (rev, bs) in revEntries)
        {
            rev.Rev = bs[0].Text.Trim(); rev.Date = bs[1].Text.Trim();
            rev.Description = bs[2].Text.Trim(); rev.By = bs[3].Text.Trim();
            if (!string.IsNullOrEmpty(rev.Rev) || !string.IsNullOrEmpty(rev.Description))
                info.Revisions.Add(rev);
        }
    }

    // ===== シート設定（母線名・グリッドサイズ） =====

    private async void OnSheetSettings(object sender, RoutedEventArgs e)
    {
        var leftBox  = new TextBox { Text = _sheet.Bus.LeftName,  PlaceholderText = "左母線名（例: R200）", Header = "左母線名" };
        var rightBox = new TextBox { Text = _sheet.Bus.RightName, PlaceholderText = "右母線名（例: S200）", Header = "右母線名" };
        var voltageBox = new TextBox { Text = _sheet.Bus.PowerLabel ?? "", PlaceholderText = "母線間電圧（例: AC200V）", Header = "電圧（母線間・任意）" };
        var colBox   = new NumberBox
        {
            Value = _sheet.Grid.Columns,
            Minimum = 2, Maximum = 20,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = "列数（2〜20）",
        };
        var rowBox   = new NumberBox
        {
            Value = _sheet.Grid.Rows,
            Minimum = 1, Maximum = 60,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = "行数（1〜60）",
        };

        var defaultCheck = new CheckBox { Content = "この母線名を新規シートの既定にする" };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(leftBox);
        panel.Children.Add(rightBox);
        panel.Children.Add(voltageBox);
        panel.Children.Add(defaultCheck);
        panel.Children.Add(colBox);
        panel.Children.Add(rowBox);

        var dialog = new ContentDialog
        {
            Title = "シート設定",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        leftBox.Loaded += (_, _) => leftBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _sheet.Bus = new BusConfig
        {
            LeftName  = leftBox.Text.Trim().Length > 0 ? leftBox.Text.Trim() : _sheet.Bus.LeftName,
            RightName = rightBox.Text.Trim().Length > 0 ? rightBox.Text.Trim() : _sheet.Bus.RightName,
            PowerLabel = voltageBox.Text.Trim().Length > 0 ? voltageBox.Text.Trim() : null,
        };

        // 新規シートの既定母線名として保存（チェック時）
        if (defaultCheck.IsChecked == true)
            _document.Settings.DefaultBus = new BusConfig
            {
                LeftName = _sheet.Bus.LeftName,
                RightName = _sheet.Bus.RightName,
                PowerLabel = _sheet.Bus.PowerLabel,
            };

        int newCols = double.IsNaN(colBox.Value) ? _sheet.Grid.Columns : (int)colBox.Value;
        newCols = Math.Clamp(newCols, 2, 20);
        int minCols = _sheet.Elements.Count > 0
            ? _sheet.Elements.Max(el => el.Pos.Column + el.CellWidth)
            : 1;
        _sheet.Grid.Columns = Math.Max(newCols, minCols);

        int newRows = double.IsNaN(rowBox.Value) ? _sheet.Grid.Rows : (int)rowBox.Value;
        newRows = Math.Clamp(newRows, 1, 60);
        int minRows = _sheet.Elements.Count > 0
            ? _sheet.Elements.Max(el => el.Pos.Row + 1)
            : 1;
        _sheet.Grid.Rows = Math.Max(newRows, minRows);

        Canvas.Invalidate();
    }

    // ===== 部品リスト（BOM）=====

    private async void OnBomEditor(object sender, RoutedEventArgs e)
    {
        // 図面中の全機器名を収集（機器表と同じロジック）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new List<(string Name, string Label, ElementKind Kind)>();
        foreach (var sheet in _document.Sheets)
            foreach (var elem in sheet.Elements.OrderBy(el => el.Pos.Row).ThenBy(el => el.Pos.Column))
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;
                if (!seen.Add(elem.DeviceName)) continue;
                var kind = PartResolver.ComponentKind(elem, _document.Library);
                devices.Add((elem.DeviceName, DeviceKindLabel(kind), kind));
            }
        devices.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        foreach (double w in new[] { 110.0, 55.0, 160.0, 120.0, 80.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void AddHeader(int col, string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 };
            Grid.SetColumn(tb, col); Grid.SetRow(tb, 0); grid.Children.Add(tb);
        }
        AddHeader(0, "機器名"); AddHeader(1, "種別"); AddHeader(2, "型式"); AddHeader(3, "メーカー"); AddHeader(4, "数量");

        var rows = new List<(string Name, ElementKind Kind, TextBox Model, TextBox Maker, NumberBox Qty)>();
        int r = 1;
        foreach (var (name, label, kind) in devices)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _document.Devices.ByName.TryGetValue(name, out var dev);

            var nameTb = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var labelTb = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var modelBox = new TextBox { Text = dev?.Model ?? "", FontSize = 12 };
            var makerBox = new TextBox { Text = dev?.Maker ?? "", FontSize = 12 };
            var qtyBox = new NumberBox
            {
                Value = dev?.Quantity ?? 1,
                Minimum = 0, Maximum = 9999, SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };

            Grid.SetColumn(nameTb, 0); Grid.SetRow(nameTb, r); grid.Children.Add(nameTb);
            Grid.SetColumn(labelTb, 1); Grid.SetRow(labelTb, r); grid.Children.Add(labelTb);
            Grid.SetColumn(modelBox, 2); Grid.SetRow(modelBox, r); grid.Children.Add(modelBox);
            Grid.SetColumn(makerBox, 3); Grid.SetRow(makerBox, r); grid.Children.Add(makerBox);
            Grid.SetColumn(qtyBox, 4); Grid.SetRow(qtyBox, r); grid.Children.Add(qtyBox);

            rows.Add((name, kind, modelBox, makerBox, qtyBox));
            r++;
        }

        FrameworkElement body = devices.Count == 0
            ? new TextBlock { Text = "機器名が設定された要素がありません。", FontSize = 12 }
            : new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };

        var dialog = new ContentDialog
        {
            Title = "部品リスト(BOM)",
            Content = body,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
            MinWidth = 560,
        };
        if (devices.Count == 0) dialog.PrimaryButtonText = "";

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // 入力を DeviceTable へ書き戻す（変更があればダーティ化）
        bool changed = false;
        foreach (var (name, kind, modelBox, makerBox, qtyBox) in rows)
        {
            string model = modelBox.Text.Trim();
            string maker = makerBox.Text.Trim();
            int qty = double.IsNaN(qtyBox.Value) ? 1 : Math.Max(0, (int)qtyBox.Value);

            if (!_document.Devices.ByName.TryGetValue(name, out var dev))
            {
                dev = new Device { Name = name, Class = MapDeviceClass(kind) };
                _document.Devices.ByName[name] = dev;
            }
            string? newModel = model.Length > 0 ? model : null;
            string? newMaker = maker.Length > 0 ? maker : null;
            if (dev.Model != newModel || dev.Maker != newMaker || dev.Quantity != qty) changed = true;
            dev.Model = newModel;
            dev.Maker = newMaker;
            dev.Quantity = qty;
        }

        if (changed)
        {
            _savedUndoDepth = -1;   // BOM 変更は Undo 対象外。確実にダーティ表示にする
            UpdateStatusExtras();
        }
    }

    private static DeviceClass MapDeviceClass(ElementKind kind) => kind switch
    {
        ElementKind.Coil or ElementKind.Timer
            or ElementKind.ContactNO or ElementKind.ContactNC => DeviceClass.Relay,
        ElementKind.PushButtonNO or ElementKind.PushButtonNC
            or ElementKind.EmergencyStop => DeviceClass.PushButton,
        ElementKind.SelectSwitch => DeviceClass.SelectSwitch,
        ElementKind.Lamp => DeviceClass.Lamp,
        ElementKind.TimerContactNO or ElementKind.TimerContactNC => DeviceClass.Timer,
        ElementKind.Terminal => DeviceClass.Terminal,
        _ => DeviceClass.Other,
    };

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
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = 8, Rows = 8 },
            Bus = new BusConfig { LeftName = "R200", RightName = "S200" },
        };
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
        var dialog = new ContentDialog
        {
            Title = "GuiEcad",
            Content = "ラダー図エディタ\nバージョン 0.1.0",
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

    // ===== ポインタ =====

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 右クリックはコンテキストメニュー専用（RightTapped で処理）。
        // 左用ロジックに流すと Canvas.CapturePointer によりタップ系ジェスチャが破棄され RightTapped が発火しない。
        // e.Handled も立てない（立てると同様に RightTapped が発火しなくなる）。
        if (e.GetCurrentPoint(Canvas).Properties.IsRightButtonPressed) return;

        // 前操作のドラッグ残骸を破棄（キャプチャ喪失等で固まるのを防ぐ）＋キーボード操作のためフォーカス確保
        _connStartRow = null;
        _frameStartMm = null;
        if (_editingElement is null && _editingComment is null && _editingRungComment is null) Canvas.Focus(FocusState.Programmatic);

        var pos = e.GetCurrentPoint(Canvas).Position;
        var (xMm, yMm) = ToWorld(pos);

        // スペースキー保持中は全ツール共通でパン（テストモード除く）
        // KeyDown フラグ＋GetKeyStateForCurrentThread の二重判定（Canvas がキーイベントを吸収する環境に対応）
        bool spaceDown = !_testMode && (_spacePanActive ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & CoreVirtualKeyStates.Down) != 0);
        if (spaceDown)
        {
            _panning = true;
            _lastPointer = pos;
            Canvas.CapturePointer(e.Pointer);
            return;
        }
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);

        // 自前ダブルクリック検出: 選択/分岐ツールは CapturePointer で DoubleTapped が来ないため、
        // ツール非依存で機器名・枠ラベルのインライン編集を起動する（作画モードのみ）。
        if (!_testMode)
        {
            var now = DateTime.UtcNow;
            bool isDouble = (now - _lastClickTime).TotalMilliseconds < DoubleClickMs
                && Math.Abs(pos.X - _lastClickPos.X) < 8 && Math.Abs(pos.Y - _lastClickPos.Y) < 8;
            _lastClickTime = now;
            _lastClickPos = pos;
            if (isDouble)
            {
                if (HitTest(row, col) is ElementInstance dhit)
                {
                    _selected = dhit; _selectedConnector = null; _selectedFrame = null;
                    ShowDeviceNameEditor(dhit);
                    Canvas.Invalidate();
                    return;
                }
                if (HitTestFrame(xMm, yMm) is GroupFrame dframe) { ShowFrameLabelEditor(dframe, pos); return; }
                // 右母線右側 ダブルクリック → 行コメント編集
                double rightBusEdge = _geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5;
                if (xMm > rightBusEdge && row >= 0 && row < _sheet.Grid.Rows)
                {
                    var existing = _sheet.RungComments.Find(rc => rc.Row == row);
                    if (existing is null) { existing = new RungComment { Row = row }; _history.Execute(new AddRungCommentCommand(_sheet, existing)); }
                    ShowRungCommentEditor(existing, pos);
                    return;
                }
            }
        }

        // テストモード
        if (_testMode && _testSession != null)
        {
            var hit = HitTest(row, col);
            if (hit?.DeviceName is string dev)
            {
                if (hit.Kind is ElementKind.PushButtonNO or ElementKind.PushButtonNC)
                {
                    _testSession.SetInput(dev, true);
                    _testPressedDevice = dev;
                    Canvas.CapturePointer(e.Pointer);
                }
                else if (hit.Kind == ElementKind.SelectSwitch)
                {
                    CycleSelectSwitch(hit);
                }
                else
                {
                    _testSession.ToggleInput(dev);
                }
                UpdateTestStatus();
                Canvas.Invalidate();
            }
            else
            {
                // 空欄クリック：作画モードと同様にパン開始
                _panning = true;
                _lastPointer = pos;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：枠ツール（ドラッグ開始・mm 連続座標）
        if (_placeFrame)
        {
            if (xMm >= 0 && yMm >= 0)
            {
                _frameStartMm = (xMm, yMm);
                _frameCurMm = (xMm, yMm);
                _selectedFrame = null;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：縦コネクタ配置（列境界をドラッグ開始）
        if (_placeConnector)
        {
            double b = _geo.BoundaryAtHalf(xMm);   // セル中央（0.5）にもスナップ
            if (b >= 0 && b <= _sheet.Grid.Columns && row >= 0)
            {
                _connStartRow = row;
                _connCurRow = row;
                _connBoundary = b;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：配置
        if (_placeKind is ElementKind kind)
        {
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns && CellEmpty(row, col, null))
            {
                var part = _document.Library?.Get(_placePartId);
                var el = new ElementInstance
                {
                    Kind = kind,
                    Pos = new GridPos(row, col),
                    PartId = part is not null ? _placePartId : null,
                    CellWidth = part?.WidthCells ?? ElementCatalog.DefaultCellWidth(kind),
                };
                _history.Execute(new PlaceElementCommand(_sheet, el));
                _selected = el;
                RefreshDevicePanel();
                RefreshPropertiesPanel();
                Canvas.Invalidate();
            }
            return;
        }

        // 作画モード：選択・移動（要素→縦コネクタ→枠の順にヒット判定）
        var hitElem = HitTest(row, col);
        if (hitElem is not null)
        {
            _selected = hitElem;
            _selectedConnector = null;
            _selectedFrame = null;
            _moving = hitElem;
            _moveStartPos = hitElem.Pos;
        }
        else if (HitTestConnector(xMm, yMm) is VerticalConnector hitConn)
        {
            _selectedConnector = hitConn;
            _movingConnector = hitConn;
            _connMoveStartColumn = hitConn.Column;
            _selected = null;
            _selectedFrame = null;
        }
        else if (HitTestFrame(xMm, yMm) is GroupFrame hitFrame)
        {
            _selectedFrame = hitFrame;
            _selected = null;
            _selectedConnector = null;
            _movingFrame = true;
            _moveFrameOriginX = hitFrame.VisualXMm ?? _geo.X(hitFrame.TopLeft.Column);
            _moveFrameOriginY = hitFrame.VisualYMm ?? (_geo.YRow(hitFrame.TopLeft.Row) - _geo.CellMm * 0.4);
            _moveFrameClickX = xMm;
            _moveFrameClickY = yMm;
        }
        else
        {
            _selected = null;
            _selectedConnector = null;
            _selectedFrame = null;
            _selectedSet.Clear();
            if (row >= 0 && col >= 0)
            {
                _rangeSelecting = true;
                _rangeStart = new GridPos(row, col);
                _rangeEnd = _rangeStart;
            }
            else
            {
                _panning = true;
                _lastPointer = pos;
            }
        }
        Canvas.CapturePointer(e.Pointer);
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;

        var (sxMm, syMm) = ToWorld(pos);
        int sRow = _geo.RowAt(syMm), sCol = _geo.ColAt(sxMm);
        StatusPos.Text = $"行: {Math.Max(sRow + 1, 1)}  列: {Math.Max(sCol + 1, 1)}";

        if (_rangeSelecting)
        {
            _rangeEnd = new GridPos(Math.Max(0, sRow), Math.Max(0, sCol));
            Canvas.Invalidate();
            return;
        }

        // 縦コネクタのドラッグ中：終端行を追従してプレビュー
        if (_connStartRow is not null)
        {
            _connCurRow = Math.Max(0, sRow);
            Canvas.Invalidate();
            return;
        }

        // 枠ドラッグ中：終端座標を追従してプレビュー（mm 連続座標）
        if (_frameStartMm is not null)
        {
            _frameCurMm = (Math.Max(0, sxMm), Math.Max(0, syMm));
            Canvas.Invalidate();
            return;
        }

        if (_movingConnector is not null)
        {
            var (xMm, _) = ToWorld(pos);
            double newCol = _geo.BoundaryAtHalf(xMm);
            if (newCol > 0 && newCol < _sheet.Grid.Columns && newCol != _movingConnector.Column)
            {
                _movingConnector.Column = newCol;
                Canvas.Invalidate();
            }
            return;
        }

        if (_movingFrame && _selectedFrame is GroupFrame movFr)
        {
            movFr.VisualXMm = _moveFrameOriginX + (sxMm - _moveFrameClickX);
            movFr.VisualYMm = _moveFrameOriginY + (syMm - _moveFrameClickY);
            Canvas.Invalidate();
            return;
        }

        if (_moving is not null)
        {
            var (xMm, yMm) = ToWorld(pos);
            int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns
                && (row != _moving.Pos.Row || col != _moving.Pos.Column)
                && CellEmpty(row, col, _moving))
            {
                _moving.Pos = new GridPos(row, col);
                Canvas.Invalidate();
            }
            return;
        }

        if (_panning)
        {
            _panX += pos.X - _lastPointer.X;
            _panY += pos.Y - _lastPointer.Y;
            _lastPointer = pos;
            Canvas.Invalidate();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rangeSelecting)
        {
            _rangeSelecting = false;
            int r1 = Math.Min(_rangeStart.Row, _rangeEnd.Row);
            int r2 = Math.Max(_rangeStart.Row, _rangeEnd.Row);
            int c1 = Math.Min(_rangeStart.Column, _rangeEnd.Column);
            int c2 = Math.Max(_rangeStart.Column, _rangeEnd.Column);
            _selectedSet = new HashSet<ElementInstance>(
                _sheet.Elements.Where(e => e.Pos.Row >= r1 && e.Pos.Row <= r2
                                        && e.Pos.Column >= c1 && e.Pos.Column <= c2));
            Canvas.ReleasePointerCapture(e.Pointer);
            Canvas.Invalidate();
            return;
        }

        // 枠ドラッグの確定（mm 連続座標で自由作成・グリッドにスナップしない）
        if (_frameStartMm is (double fsx, double fsy))
        {
            _frameStartMm = null;
            double x1 = Math.Min(fsx, _frameCurMm.X), y1 = Math.Min(fsy, _frameCurMm.Y);
            double wMm = Math.Abs(_frameCurMm.X - fsx), hMm = Math.Abs(_frameCurMm.Y - fsy);
            // 半セル未満の極小ドラッグは誤操作とみなし無視
            if (wMm >= _geo.CellMm * 0.5 && hMm >= _geo.CellMm * 0.5)
            {
                // 行挿入/削除ロジック用のグリッド近似値（フォールバック）も併せて設定
                int c1 = Math.Max(0, _geo.ColAt(x1));
                int r1 = Math.Max(0, _geo.RowAt(y1 + _geo.CellMm * 0.4));
                int gw = Math.Max(1, (int)Math.Round(wMm / _geo.CellMm));
                int gh = Math.Max(1, (int)Math.Round(hMm / _geo.CellMm));
                var frame = new GroupFrame
                {
                    TopLeft = new GridPos(r1, c1),
                    Width = gw,
                    Height = gh,
                    VisualXMm = x1,
                    VisualYMm = y1,
                    VisualWidthMm = wMm,
                    VisualHeightMm = hMm,
                };
                _history.Execute(new AddFrameCommand(_sheet, frame));
                _selectedFrame = frame;
            }
            Canvas.ReleasePointerCapture(e.Pointer);
            Canvas.Invalidate();
            return;
        }

        // 縦コネクタ配置の確定（開始行と終端行が異なれば作成）
        if (_connStartRow is int sr)
        {
            int top = Math.Min(sr, _connCurRow), bot = Math.Max(sr, _connCurRow);
            if (top != bot)
                _history.Execute(new AddConnectorCommand(_sheet,
                    new VerticalConnector { Column = _connBoundary, TopRow = top, BottomRow = bot }));
            _connStartRow = null;
            Canvas.ReleasePointerCapture(e.Pointer);
            Canvas.Invalidate();
            return;
        }

        // テストモード：PushButton リリース
        if (_testMode && _testSession != null && _testPressedDevice != null)
        {
            _testSession.SetInput(_testPressedDevice, false);
            _testPressedDevice = null;
            UpdateTestStatus();
            Canvas.Invalidate();
        }

        // 縦コネクタドラッグ移動の確定
        if (_movingConnector is not null && _movingConnector.Column != _connMoveStartColumn)
            _history.Execute(new MoveConnectorCommand(_sheet, _movingConnector, _connMoveStartColumn, _movingConnector.Column));
        _movingConnector = null;

        // 枠ドラッグ移動の確定
        if (_movingFrame && _selectedFrame is GroupFrame movFr2
            && (movFr2.VisualXMm != _moveFrameOriginX || movFr2.VisualYMm != _moveFrameOriginY))
            _history.Execute(new MoveFrameCommand(_sheet, movFr2, _moveFrameOriginX, _moveFrameOriginY,
                movFr2.VisualXMm!.Value, movFr2.VisualYMm!.Value));
        _movingFrame = false;

        // ドラッグ移動のコマンド登録（位置が変わっていれば）
        if (_moving is not null && _moving.Pos != _moveStartPos)
            _history.Execute(new MoveElementCommand(_sheet, _moving, _moveStartPos, _moving.Pos));

        _moving = null;
        _panning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>ドラッグ/パン等の進行中ポインタ状態を破棄する（ツール切替・モード切替・キャプチャ喪失時の保険）。</summary>
    private void ResetDragState()
    {
        _panning = false;
        _moving = null;
        _movingFrame = false;
        _movingConnector = null;
        _rangeSelecting = false;
        _connStartRow = null;
        _frameStartMm = null;
    }

    // キャプチャ喪失（フライアウト表示・フォーカス移動等）で PointerReleased が来ないと
    // _panning 等が立ったまま残り、次の操作がパンになってしまう。確実に破棄する。
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_testMode && _testSession != null && _testPressedDevice != null)
        {
            _testSession.SetInput(_testPressedDevice, false);
            UpdateTestStatus();
        }
        _testPressedDevice = null;
        ResetDragState();
        Canvas.Invalidate();
    }

    // ===== インライン編集（機器名 / 枠ラベル）。起動は OnPointerPressed の自前ダブルクリック検出 =====

    private void ShowFrameLabelEditor(GroupFrame frame, Windows.Foundation.Point screenPos)
    {
        _editingFrame = frame;
        FrameLabelBox.Text = frame.Label ?? string.Empty;
        double scale = DipsPerMm * _zoom;
        double x = _geo.X(frame.TopLeft.Column) * scale + _panX;
        double y = (_geo.YRow(frame.TopLeft.Row) - _geo.CellMm * 0.4) * scale + _panY - 18;
        FrameLabelBox.Margin = new Thickness(x, Math.Max(0, y), 0, 0);
        FrameLabelBox.Visibility = Visibility.Visible;
        FrameLabelBox.Focus(FocusState.Programmatic);
        FrameLabelBox.SelectAll();
    }

    private void CommitFrameLabel(bool accept)
    {
        if (_editingFrame is null) return;
        var frame = _editingFrame;
        _editingFrame = null;
        FrameLabelBox.Visibility = Visibility.Collapsed;
        if (accept)
        {
            string newLabel = FrameLabelBox.Text.Trim();
            if (newLabel != (frame.Label ?? string.Empty))
                _history.Execute(new RenameFrameCommand(_sheet, frame, frame.Label ?? string.Empty, newLabel));
        }
        Canvas.Invalidate();
    }

    private void OnFrameLabelBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitFrameLabel(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitFrameLabel(accept: false); e.Handled = true; }
    }

    private void OnFrameLabelBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingFrame is not null) CommitFrameLabel(accept: true);
    }

    private void ShowDeviceNameEditor(ElementInstance elem)
    {
        _editingElement = elem;
        DeviceNameBox.Text = elem.DeviceName ?? string.Empty;

        double scale = DipsPerMm * _zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _panX + cellDip * 0.5 - 45;
        double y = _geo.YRow(elem.Pos.Row) * scale + _panY - 14;
        DeviceNameBox.Margin = new Thickness(x, y, 0, 0);
        DeviceNameBox.Visibility = Visibility.Visible;
        DeviceNameBox.Focus(FocusState.Programmatic);
        DeviceNameBox.SelectAll();
    }

    private void CommitDeviceName(bool accept)
    {
        if (_editingElement is null) return;
        var elem = _editingElement;
        _editingElement = null;
        DeviceNameBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newName = DeviceNameBox.Text.Trim();
            if (newName != (elem.DeviceName ?? string.Empty))
            {
                _history.Execute(new RenameDeviceCommand(_sheet, elem, elem.DeviceName, newName));
                RefreshDevicePanel();
            }
        }
        Canvas.Invalidate();
    }

    private void OnDeviceNameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitDeviceName(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitDeviceName(accept: false); e.Handled = true; }
    }

    private void OnDeviceNameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingElement is not null) CommitDeviceName(accept: true);
    }

    private void ShowCommentEditor(ElementInstance elem)
    {
        _editingComment = elem;
        CommentBox.Text = elem.Comment ?? string.Empty;

        double scale = DipsPerMm * _zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _panX + cellDip * 0.5 - 45;
        double y = _geo.YRow(elem.Pos.Row) * scale + _panY - 14 + 28;   // 機器名Boxの下
        CommentBox.Margin = new Thickness(x, y, 0, 0);
        CommentBox.Visibility = Visibility.Visible;
        CommentBox.Focus(FocusState.Programmatic);
        CommentBox.SelectAll();
    }

    private void CommitComment(bool accept)
    {
        if (_editingComment is null) return;
        var elem = _editingComment;
        _editingComment = null;
        CommentBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newComment = CommentBox.Text.Trim();
            string? old = elem.Comment;
            if (newComment != (old ?? string.Empty))
            {
                string? next = string.IsNullOrEmpty(newComment) ? null : newComment;
                _history.Execute(new SetCommentCommand(_sheet, elem, old, next));
            }
        }
        Canvas.Invalidate();
    }

    private void OnCommentBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitComment(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitComment(accept: false); e.Handled = true; }
    }

    private void OnCommentBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingComment is not null) CommitComment(accept: true);
    }

    private void ShowRungCommentEditor(RungComment rc, Windows.Foundation.Point screenPos)
    {
        _editingRungComment = rc;
        RungCommentBox.Text = rc.Text;

        double scale = DipsPerMm * _zoom;
        double rightEdgeDip = (_geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5) * scale + _panX + 4;
        double y = _geo.YRow(rc.Row) * scale + _panY - 14;
        RungCommentBox.Margin = new Thickness(rightEdgeDip, y, 0, 0);
        RungCommentBox.Visibility = Visibility.Visible;
        RungCommentBox.Focus(FocusState.Programmatic);
        RungCommentBox.SelectAll();
    }

    private void CommitRungComment(bool accept)
    {
        if (_editingRungComment is null) return;
        var rc = _editingRungComment;
        _editingRungComment = null;
        RungCommentBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newText = RungCommentBox.Text.Trim();
            if (newText != rc.Text)
                _history.Execute(new SetRungCommentCommand(_sheet, rc, rc.Text, newText));
        }
        Canvas.Invalidate();
    }

    private void OnRungCommentBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitRungComment(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitRungComment(accept: false); e.Handled = true; }
    }

    private void OnRungCommentBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingRungComment is not null) CommitRungComment(accept: true);
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
            AddItem(menu, "コメント編集(F2)", () => { _selected = hitElem; ShowCommentEditor(hitElem); });
            AddItem(menu, "機器名変更(Enter)", () => { _selected = hitElem; ShowDeviceNameEditor(hitElem); });
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
