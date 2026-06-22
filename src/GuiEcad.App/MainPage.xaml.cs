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
// x:Name="Canvas"（CanvasControl）と型名 Canvas が衝突するため、添付プロパティ用に別名を使う。
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    private const double DipsPerMm = CanvasViewport.DipsPerMm;

    // ドキュメント / シート
    private LadderDocument _document;
    private Sheet _sheet;
    private string? _currentPath;
    private readonly GridGeometry _geo = new(9.0, 20.0);   // CellMm / MarginMm。RenderOptions.MarginMm と一致させる。

    // 図形フォルダ管理（実フォルダ「図形/」「図形/自作/」のマスター・呼び出し元）
    private PartFolderStore _folderStore = null!;
    private MenuBarItem _shapeMenuItem = null!;   // 「図形(G)」。再構築時に丸ごと差し替える
    private IReadOnlyList<PartFolderEntry> _folderEntries = Array.Empty<PartFolderEntry>();
    private readonly Dictionary<string, PartFolderEntry> _folderPartMap = new();

    // 表示状態
    private bool _connectivityCheck;
    private bool _testMode;
    private bool _devicePanelVisible;
    // ズーム/パン状態と座標変換は CanvasViewport へ委譲（UI 非依存）。
    private readonly CanvasViewport _viewport = new();

    // 作画状態
    private ElementKind? _placeKind;
    private string? _placePartId;   // 配置対象が自作パーツのとき PartLibrary の Id（組込み種別なら null）
    private string? _placeOrient;   // 主回路記号の配置時の向き（"V"/"H"・通常記号は null）
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

    /// <summary>機器名・コメント・枠ラベル・行コメントいずれかをインライン編集中か。
    /// 編集中は Del/BS を要素削除に使わず、テキスト編集に委ねるための判定。</summary>
    private bool IsInlineEditing =>
        _editingElement is not null || _editingComment is not null
        || _editingRungComment is not null || _editingFrame is not null;

    /// <summary>
    /// テキスト入力にフォーカスがあるか（キャンバスのインライン編集・プロパティパネル・検索バー等）。
    /// true の間は Del/BS を要素削除に使わず、テキスト編集へ委ねる。
    /// プロパティパネルの TextBox はインライン編集フラグを立てないため、フォーカスで判定する。
    /// NumberBox は内部 TextBox を持つので is TextBox で拾える。
    /// </summary>
    private bool IsTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(this.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox;
    }

    // Dirty フラグ（保存時の UndoDepth を記録して比較）
    private int _savedUndoDepth;

    // 設置場所枠（GroupFrame）
    private bool _placeFrame;
    // 枠ドラッグ作成は mm 座標で自由配置（グリッドにスナップしない）
    private (double X, double Y)? _frameStartMm;
    private (double X, double Y) _frameCurMm;
    private GroupFrame? _selectedFrame;
    private GroupFrame? _editingFrame;

    // 作図ガイドの薄いグリッド表示
    private bool _showGrid;

    // 自由直線ツール（主回路用・mm 座標・格子点スナップ）
    private bool _placeLine;
    private (double X, double Y)? _lineStartMm;
    private (double X, double Y) _lineCurMm;
    private FreeLine? _selectedLine;
    // 接続点（●）ツール
    private bool _placeDot;
    private ConnectionDot? _selectedDot;
    // 自由直線のドラッグ移動
    private bool _movingLine;
    private (double X, double Y) _lineMoveClick;
    private (double X1, double Y1, double X2, double Y2) _lineOrig;
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
    private HashSet<VerticalConnector> _selectedConnectorSet = new();   // 範囲選択に含まれる分岐線（縦コネクタ）
    private HashSet<FreeLine> _selectedLineSet = new();                 // 範囲選択に含まれる自由直線

    // 複数選択（要素＋分岐線＋自由直線）をまとめて解除する。
    private void ClearMultiSelection() { _selectedSet.Clear(); _selectedConnectorSet.Clear(); _selectedLineSet.Clear(); }

    // コピー・ペースト
    private sealed record ClipboardData(
        List<ElementInstance> Elements, List<VerticalConnector> Connectors,
        List<FreeLine> FreeLines, int OriginRow, int OriginCol);
    private ClipboardData? _clipboard;
    private GridPos _hoverCell;   // 直近のマウス位置セル（ペースト基準・OnPointerMoved で更新）

    // 検索・置換（全シート横断）状態とロジックは FindController へ委譲。
    private readonly FindController _find = new();

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
        LoadTheme();
        LoadPaletteState();   // ツールパレットのドック/フロート状態・位置を復元
        _document = new LadderDocument();
        _document.Sheets.Add(CreateEmptySheet());
        _sheet = _document.Sheets[0];
        Loaded += async (_, _) =>
        {
            await LoadToolIconsAsync();
            RebuildNavTree();
            InitShapeFolder();        // _folderEntries を先に用意（その他▼の自作図形に必要）
            RebuildOtherPartMenu();
        };
    }

    // ===== テーマ（ライト/ダーク） =====

    private static string ThemeSettingPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GuiEcad", "ui-theme.txt");

    // 起動時に保存済みテーマを適用する（未保存ならシステム既定のまま）。
    private void LoadTheme()
    {
        try
        {
            if (System.IO.File.Exists(ThemeSettingPath) &&
                System.IO.File.ReadAllText(ThemeSettingPath).Trim() == "Dark")
            {
                RootGrid.RequestedTheme = ElementTheme.Dark;
                DarkModeMenuItem.IsChecked = true;
            }
        }
        catch { /* 設定読込失敗は無視（既定テーマで続行） */ }
    }

    private void OnDarkModeToggle(object sender, RoutedEventArgs e)
    {
        var theme = DarkModeMenuItem.IsChecked ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.RequestedTheme = theme;
        _ = LoadToolIconsAsync();   // テーマに合わせてツールアイコンを再生成（黒/明色）
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ThemeSettingPath)!);
            System.IO.File.WriteAllText(ThemeSettingPath, theme == ElementTheme.Dark ? "Dark" : "Light");
        }
        catch { /* 設定保存失敗は致命的でない */ }
    }

    private void OnGridToggle(object sender, RoutedEventArgs e)
    {
        _showGrid = GridMenuItem.IsChecked;
        Canvas.Invalidate();
    }


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
            drcAll.AddRange(DesignRuleCheck.CheckSeriesCoils(_sheet, sheetNet));
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

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { ToggleFindBar(); args.Handled = true; }

    // ===== 削除 =====

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selectedSet.Count > 0 || _selectedConnectorSet.Count > 0 || _selectedLineSet.Count > 0)
        {
            var cmds = _selectedSet
                .Select(e => (IUndoCommand)new DeleteElementCommand(_sheet, e))
                .Concat(_selectedConnectorSet.Select(c => (IUndoCommand)new DeleteConnectorCommand(_sheet, c)))
                .Concat(_selectedLineSet.Select(l => (IUndoCommand)new DeleteFreeLineCommand(_sheet, l)))
                .ToList();
            _history.Execute(new BatchCommand(_sheet, cmds));
            ClearMultiSelection();
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
        else if (_selectedLine is not null)
        {
            _history.Execute(new DeleteFreeLineCommand(_sheet, _selectedLine));
            _selectedLine = null;
            Canvas.Invalidate();
        }
        else if (_selectedDot is not null)
        {
            _history.Execute(new DeleteDotCommand(_sheet, _selectedDot));
            _selectedDot = null;
            Canvas.Invalidate();
        }
    }

    // 点(xMm,yMm)に最も近い接続点を当たり判定半径内で返す。
    private ConnectionDot? HitTestDot(double xMm, double yMm)
    {
        double best = _geo.CellMm * 0.25;   // 当たり判定半径(mm)
        ConnectionDot? hit = null;
        foreach (var d in _sheet.ConnectionDots)
        {
            double dist = Math.Sqrt((d.XMm - xMm) * (d.XMm - xMm) + (d.YMm - yMm) * (d.YMm - yMm));
            if (dist <= best) { best = dist; hit = d; }
        }
        return hit;
    }

    // 点(xMm,yMm)に最も近い自由直線を当たり判定半径内で返す。
    private FreeLine? HitTestFreeLine(double xMm, double yMm)
    {
        double bestDist = _geo.CellMm * 0.3;   // 当たり判定半径(mm)
        FreeLine? best = null;
        foreach (var fl in _sheet.FreeLines)
        {
            double d = DistPointToSegment(xMm, yMm, fl.X1Mm, fl.Y1Mm, fl.X2Mm, fl.Y2Mm);
            if (d <= bestDist) { bestDist = d; best = fl; }
        }
        return best;
    }

    private static double DistPointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
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
        // Ctrl+Z/Y/C/V/F 等の修飾キー系は KeyboardAccelerator（RootGrid）側で処理する。
        // CanvasControl がフォーカスを持つと Page の KeyDown が届かない場合があるため、
        // 確実性が要るショートカットはアクセラレータに統一する。
        switch (e.Key)
        {
            case VirtualKey.Back:
                // テキスト入力中（インライン編集・プロパティパネル・検索バー）は要素削除しない。
                if (IsInlineEditing || IsTextInputFocused()) break;
                DeleteSelected(); e.Handled = true; break;

            case VirtualKey.F5: ActivateTool("ContactNO"); e.Handled = true; break;
            case VirtualKey.F6: ActivateTool("ContactNC"); e.Handled = true; break;
            case VirtualKey.F7: ActivateTool("Coil"); e.Handled = true; break;
            case VirtualKey.F8: ActivateTool("PushButtonNO"); e.Handled = true; break;

            case VirtualKey.Escape:
                if (_rangeSelecting) { _rangeSelecting = false; ClearMultiSelection(); Canvas.Invalidate(); e.Handled = true; break; }
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

    private (double X, double Y) ToWorld(Point dip) => _viewport.ToWorld(dip);

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

    private void OnConnectivityToggle(object sender, RoutedEventArgs e)
    {
        _connectivityCheck = ConnectivityToggle.IsChecked == true;
        Canvas.Invalidate();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.2, ViewCenter());
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.2, ViewCenter());

    private void OnFit(object sender, RoutedEventArgs e)
    {
        _viewport.Reset();
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
        _viewport.ZoomBy(factor, at);
        Canvas.Invalidate();
    }

    // ===== 初期シート =====

    // 起動時・新規作成時の空シート（要素なし）。
    private static Sheet CreateEmptySheet() => new()
    {
        Grid = new GridSpec { Columns = 8, Rows = 8 },
        Bus = new BusConfig { LeftName = "R200", RightName = "S200" },
    };
}
