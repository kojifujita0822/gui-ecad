# 操作機能 実装プラン（優先順位付き）

> **作成**: 2026-06-20。全候補は [operation-features-plan.md](operation-features-plan.md) を正典とする。
> 本ファイルは「何を・どの順で・どう実装するか」に特化した実行計画。

---

## 依存関係マップ

```
§6 スクロール        ── 独立
§8 枠ヒットテスト    ── 独立（§10の前提）
§2 縦コネクタ移動   ── 独立（コマンド実装済み、ドラッグUIのみ）
§7 右クリック基盤   ── 独立（§5・§9 の UI に影響）
    └─ §5 行挿入・削除（右クリックUIで使う）
    └─ §9 枠線種選択（右クリックUIで使う）
§8 →  §10 枠の移動（ヒットテスト精細化後の方が UX 良い）
§7 + §8 + §10 → §4 範囲選択・コピー・ペースト（選択状態の整備が前提）
§1 列追加・削除      ── キー競合要確認（ユーザー判断事項）
```

---

## Phase A — 小修正（独立・各 30 分以内）

### A1: §6 全モードの空白ドラッグスクロール

**変更ファイル**: `MainPage.xaml.cs` のみ  
**変更量**: 10〜15 行

```csharp
// OnPointerPressed の先頭（最優先チェック）に追加
var pt = e.GetCurrentPoint(Canvas);

// 中ボタン → 常時パン開始（ツール非依存）
if (pt.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
{
    _panStartPos = pt.Position;
    _panStartX = _panX; _panStartY = _panY;
    _isPanning = true;
    Canvas.CapturePointer(e.Pointer);
    ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    e.Handled = true;
    return;
}
```

`OnPointerMoved` / `OnPointerReleased` でも `_isPanning` を既存パン処理と共用すれば
既存コード変更は最小。

**確認事項**: `_isPanning` フラグが既存の選択モードパンと共有できるか確認。

---

### A2: §8 枠のヒットテスト精細化

**変更ファイル**: `MainPage.xaml.cs` の `HitTestFrame()` のみ  
**変更量**: 10 行

```csharp
private GroupFrame? HitTestFrame(double xMm, double yMm)
{
    // margin をズームに追従させ、最大 3mm に制限（現在は CellMm*0.35 固定で広すぎる）
    double margin = Math.Min(_geo.CellMm * 0.15, 3.0);
    var hits = new List<GroupFrame>();

    foreach (var f in _sheet.Frames)
    {
        double fx = _geo.X(f.TopLeft.Column);
        double fy = _geo.YRow(f.TopLeft.Row) - _geo.CellMm * 0.4;
        double fw = f.Width * _geo.CellMm;
        double fh = f.Height * _geo.CellMm;
        bool insideX = xMm >= fx - margin && xMm <= fx + fw + margin;
        bool insideY = yMm >= fy - margin && yMm <= fy + fh + margin;
        bool onBorderX = xMm <= fx + margin || xMm >= fx + fw - margin;
        bool onBorderY = yMm <= fy + margin || yMm >= fy + fh - margin;
        if (insideX && insideY && (onBorderX || onBorderY))
            hits.Add(f);
    }
    // 重なる場合は面積最小（最も精細な枠）を優先
    return hits.Count == 0 ? null
         : hits.MinBy(f => f.Width * f.Height);
}
```

---

## Phase B — 右クリック基盤（§7）

右クリックメニューは §5・§9 の UI の前提。まず取得方法を確認してから実装。

### B0: 右クリック取得方法の検証（5 分）

`OnPointerPressed` の先頭に以下を一時追加してビルド・動作確認:

```csharp
if (e.GetCurrentPoint(Canvas).Properties.IsRightButtonPressed)
{
    // 動作確認用: ステータスバーに表示
    StatusWarn.Text = "右クリック検出";
    return;
}
```

- **動いた** → このルートで実装（WndProc 不要、フック全削除可）
- **動かない** → WndProc の `SetWindowLongW` → `SetWindowLongPtrW` に変更してリトライ

### B1: §7 右クリックメニュー本体（作画モード）

**変更ファイル**: `MainPage.xaml.cs`  
**変更量**: 50〜70 行

B0 で確定した取得方法でトリガし、`ShowDrawingContextMenu(Point pos)` を新規追加:

```csharp
private void ShowDrawingContextMenu(Point pos)
{
    if (_testMode) return; // テストモードは ShowContactContextMenu が担当
    var (xMm, yMm) = ToWorld(pos);
    int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);

    var menu = new MenuFlyout();

    if (HitTest(row, col) is ElementInstance hitElem)
    {
        // 要素メニュー
        AddItem(menu, "削除(Del)",    () => _history.Execute(new DeleteElementCommand(_sheet, hitElem)));
        AddItem(menu, "コメント編集", () => StartCommentEdit(hitElem));
        AddItem(menu, "機器名変更",   () => ShowDeviceNameEditor(hitElem));
    }
    else if (HitTestConnector(xMm, yMm) is VerticalConnector hitVc)
    {
        AddItem(menu, "縦コネクタ削除", () => _history.Execute(new DeleteConnectorCommand(_sheet, hitVc)));
    }
    else if (HitTestFrame(xMm, yMm) is GroupFrame hitFrame)
    {
        AddItem(menu, "ラベル編集", () => ShowFrameLabelEditor(hitFrame, pos));
        AddItem(menu, "削除",       () => _history.Execute(new DeleteFrameCommand(_sheet, hitFrame)));
        menu.Items.Add(new MenuFlyoutSeparator());
        // 線種サブメニュー（§9 実装後に追加）
    }
    else if (row >= 0)
    {
        // 空白行メニュー
        AddItem(menu, $"行 {row + 1} の前に行を挿入", () => InsertRowAt(row));
        AddItem(menu, "末尾に行を追加", () => _history.Execute(new InsertLastRowCommand(_sheet)));
    }

    if (menu.Items.Count > 0) { menu.ShowAt(Canvas, pos); Canvas.Invalidate(); }
}

private static void AddItem(MenuFlyout m, string text, Action click)
{
    var item = new MenuFlyoutItem { Text = text };
    item.Click += (_, _) => click();
    m.Items.Add(item);
}
```

---

## Phase C — 行操作（§5）

### C1: §5 選択位置への行挿入・削除

**変更ファイル**: `ElementCommands.cs`（コマンド）、`MainPage.xaml.cs`（UI）  
**変更量**: 60〜80 行

#### InsertRowCommand（新規）

```csharp
internal sealed class InsertRowCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly int _targetRow; // この行の「前」に挿入

    public InsertRowCommand(Sheet sheet, int targetRow) { _sheet = sheet; _targetRow = targetRow; }
    public Sheet Target => _sheet;

    public void Execute()
    {
        _sheet.Grid.Rows++;
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row >= _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row + 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow >= _targetRow) c.TopRow++;
            if (c.BottomRow >= _targetRow) c.BottomRow++;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row >= _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row + 1 };
            else if (f.TopLeft.Row + f.Height > _targetRow) f.Height++;
        foreach (var rc in _sheet.RungComments)
            if (rc.Row >= _targetRow) rc.Row++;
    }

    public void Undo()
    {
        _sheet.Grid.Rows--;
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row > _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row - 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow > _targetRow) c.TopRow--;
            if (c.BottomRow > _targetRow) c.BottomRow--;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row > _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row - 1 };
            else if (f.TopLeft.Row + f.Height > _targetRow) f.Height--;
        foreach (var rc in _sheet.RungComments)
            if (rc.Row > _targetRow) rc.Row--;
    }
}
```

#### DeleteRowCommand（DeleteLastRowCommand の汎化）

`DeleteLastRowCommand` の `lastRow = _sheet.Grid.Rows - 1` の部分を
`private readonly int _targetRow` に置き換えるだけ。既存コードを流用可能。

#### UI 変更

`OnAddRow` / `OnRemoveRow` を変更:
```csharp
private void OnAddRow(object sender, RoutedEventArgs e)
{
    int targetRow = _selected?.Pos.Row ?? _sheet.Grid.Rows; // 未選択なら末尾
    _history.Execute(targetRow < _sheet.Grid.Rows
        ? new InsertRowCommand(_sheet, targetRow)
        : new InsertLastRowCommand(_sheet));
    Canvas.Invalidate();
}
```

右クリックメニュー（§7 B1）の `InsertRowAt(row)` もここで実装。

---

## Phase D — 枠の改善（§10・§9）

A2（§8 ヒットテスト精細化）完了後に実施。

### D1: §10 枠の移動

**変更ファイル**: `ElementCommands.cs`（コマンド）、`MainPage.xaml.cs`（ドラッグ UI）  
**変更量**: 50〜60 行

#### MoveFrameCommand（新規）

```csharp
internal sealed class MoveFrameCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    private readonly GridPos _from, _to;
    public MoveFrameCommand(Sheet s, GroupFrame f, GridPos from, GridPos to)
        { _sheet = s; _frame = f; _from = from; _to = to; }
    public Sheet Target => _sheet;
    public void Execute() => _frame.TopLeft = _to;
    public void Undo()    => _frame.TopLeft = _from;
}
```

#### ドラッグ UI

`MainPage.xaml.cs` に状態変数を追加:
```csharp
private bool _movingFrame;
private GridPos _moveFrameOriginCell; // ドラッグ開始時の TopLeft
private GridPos _moveFrameClickOffset; // クリック位置と TopLeft の差
```

`OnPointerPressed`: `HitTestFrame` ヒット時に `_movingFrame = true` と offset を保存。  
`OnPointerMoved`: `_movingFrame` なら `_selectedFrame.TopLeft` をプレビュー更新。  
`OnPointerReleased`: 移動があれば `MoveFrameCommand` を実行、なければ位置を戻す。

---

### D2: §9 枠線種選択

**変更ファイル**: `Element.cs`・`DiagramRenderer.cs`・`GcadSerializer.cs`・`MainPage.xaml.cs`  
**変更量**: 各 5〜10 行（小さい変更が散在）

1. `Element.cs`: `public LineStyle BorderStyle { get; set; } = LineStyle.Dashed;`
2. `DiagramRenderer.DrawFrames`: `var stroke = _theme.Get(StrokeRole.GroupFrame) with { Style = f.BorderStyle };`
3. `GcadSerializer`: `GroupFrame` の JSON に `borderStyle` フィールドを追加（省略 = `Dashed`）
4. `ElementCommands.cs`: `SetFrameBorderStyleCommand(Sheet, GroupFrame, LineStyle from, LineStyle to)`
5. §7 B1 の枠メニューにサブメニュー追加:
   ```csharp
   var sub = new MenuFlyoutSubItem { Text = "線種" };
   foreach (var style in new[] { LineStyle.Solid, LineStyle.Dashed, LineStyle.Dotted })
   {
       var s = style; // closure
       var item = new MenuFlyoutItem { Text = s.ToString() };
       item.Click += (_, _) => _history.Execute(
           new SetFrameBorderStyleCommand(_sheet, hitFrame, hitFrame.BorderStyle, s));
       sub.Items.Add(item);
   }
   menu.Items.Add(sub);
   ```

---

## Phase E — 縦コネクタ移動（§2）

**変更ファイル**: `MainPage.xaml.cs` のみ（`MoveConnectorCommand` は既に実装済み）  
**変更量**: 30〜40 行

`_selectedConnector` を選択後ドラッグで列移動（`MoveConnectorCommand` を発行）。
枠の移動（D1）と同じパターンを流用。

---

## Phase F — 範囲選択・コピー・ペースト（§4）

最も変更量が大きい。Phase A〜E が安定してから着手推奨。

### F1: 範囲選択 UI

状態変数:
```csharp
private bool _rangeSelecting;
private GridPos _rangeStart, _rangeEnd;
private HashSet<ElementInstance> _selectedSet = new();
```

`OnPointerPressed`（選択モード・空白）: `_rangeSelecting = true`, `_rangeStart = cell`  
`OnPointerMoved`: `_rangeEnd` 更新 → ゴムバンド矩形を `Canvas.Invalidate()`  
`OnPointerReleased`: 矩形内の要素を `_selectedSet` に収集  

描画（`OnDraw`）: 選択中の各要素に半透明ハイライト + ゴムバンド枠（ダッシュ線）

### F2: コピー（Ctrl+C）

```csharp
private ClipboardData? _clipboard;

private void CopySelection()
{
    if (_selectedSet.Count == 0 && _selected is null) return;
    var targets = _selectedSet.Count > 0 ? _selectedSet : new HashSet<ElementInstance> { _selected! };
    int minRow = targets.Min(e => e.Pos.Row), minCol = targets.Min(e => e.Pos.Column);
    _clipboard = new ClipboardData
    {
        Elements = targets.Select(e => e.DeepClone() with
        {
            Pos = new GridPos(e.Pos.Row - minRow, e.Pos.Column - minCol)
        }).ToList(),
    };
}
```

### F3: ペースト（Ctrl+V）

```csharp
private void PasteSelection()
{
    if (_clipboard is null) return;
    int targetRow = _selected?.Pos.Row ?? _sheet.Grid.Rows - 1;
    int targetCol = _selected?.Pos.Column ?? 0;
    var cmds = _clipboard.Elements.Select(e =>
    {
        var placed = e.DeepClone();
        placed.Pos = new GridPos(targetRow + e.Pos.Row, targetCol + e.Pos.Column);
        placed.DeviceName = ResolveUniqueName(placed.DeviceName); // CR1 → CR1_1
        return (IUndoCommand)new PlaceElementCommand(_sheet, placed);
    }).ToList();
    _history.ExecuteBatch(cmds); // BatchCommand（新規追加が必要）
    Canvas.Invalidate();
}
```

`ExecuteBatch` は `_history` に `BatchCommand` を追加するか、個別に `Execute` して
後で Undo をまとめる仕組みが必要（設計要検討）。

---

## 実装順序まとめ

| フェーズ | 候補 | 推定工数 | 前提 |
|---|---|---|---|
| **A1** | §6 中ボタンスクロール | 30分 | なし |
| **A2** | §8 枠ヒットテスト精細化 | 20分 | なし |
| **B0** | §7 右クリック取得確認 | 5分 | なし |
| **B1** | §7 作画モード右クリックメニュー | 2h | B0 |
| **C1** | §5 選択行への行挿入・削除 | 1.5h | なし（B1と並行可） |
| **D1** | §10 枠の移動 | 1.5h | A2 |
| **D2** | §9 枠線種選択 | 1h | B1, D1 |
| **E** | §2 縦コネクタ移動 | 1h | なし |
| **F1** | §4 範囲選択 | 3h | A2, B1 |
| **F2/F3** | §4 コピー・ペースト | 2h | F1 |

**推奨着手順**: A1 → A2 → B0 → B1 → C1 → D1 → D2  
→ その後 E（縦コネクタ移動）→ F（範囲選択・コピペ）

---

## 未解決・ユーザー判断事項

- **§1 列追加・削除**: `Ctrl++/−` はズームと競合。`Ctrl+Shift+Right/Left` 等への変更要否はユーザー確認。
- **§4 BatchCommand**: `_history.ExecuteBatch` 設計（現状 `CommandHistory` に batch 機能なし）。ペースト時 Undo を 1操作にまとめるため要追加。
- **§4 DeepClone**: `ElementInstance` に `DeepClone()` メソッドがなければ追加が必要。
