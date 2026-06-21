# 操作機能 追加プラン

> ユーザー要望による GUI 操作機能の追加計画。**実装は未着手**（要望を順次ここに溜める）。
> 関連: [next-plan.md](next-plan.md)（自作パーツ）/ [todo.md](todo.md) / [drawing-spec.md](drawing-spec.md)。
>
> 直近で実装済み（本プランの前提）: 横線の縦コネクタ終端・機器名の Enter/ダブルクリック編集・縦コネクタのセル中央配置。

## 要望一覧

### 1. 列の追加・削除（増減）
- **内容**: シートの列数を増減する。
  - キー操作: `Ctrl + "+"` で列追加、`Ctrl + "-"` で列削除。
  - 右クリックメニュー（Canvas のコンテキストメニュー）からも増減。
- **現状**: `GridSpec.Columns`(int) を保持。母線は左=境界0・右=境界 `Columns`。列の挿入/削除 UI は無い。
- **留意点 / 設計判断**:
  - **`Ctrl++` / `Ctrl+-` は現在ズーム（拡大/縮小）の表示ラベルに使用**（`MainPage.xaml` の「拡大(+)」「縮小(-)」）。列増減へ割り当てるならズームのキーを変更するか、修飾キーを変える判断が必要。
  - 挿入/削除の**位置**を決める（末尾固定か、右クリックした列の前/後か）。
  - 列削除時は、削除列より右にある要素 (`ElementInstance.Pos.Column`)・縦コネクタ (`VerticalConnector.Column`)・枠 (`GroupFrame`) の列を詰める。削除列に要素がある場合の扱い（禁止 / 巻き込み削除）を決める。
  - Undo/Redo 対応（`InsertColumnCommand` / `DeleteColumnCommand` を `_history` 経由で）。
  - 縦コネクタはセル中央（col+0.5）配置のため、列詰め時の位置補正に注意。

### 2. 縦棒（縦コネクタ）の移動
- **内容**: 配置済みの縦コネクタ（分岐の縦棒）を別の列・行へ移動する。
- **現状**: 選択 (`_selectedConnector` / `HitTestConnector`) と削除 (`DeleteConnectorCommand`) はあるが、**移動は未実装**。配置はセル中央スナップ。
- **実装方針**: 選択中の縦コネクタをドラッグで移動（列はセル中央スナップ、上下端は行スナップ）。`MoveConnectorCommand` を追加して Undo 対応。`TopRow`/`BottomRow` の変更可否も検討。

### 3. 右母線の右側へコメント記入
- **内容**: 各回路行（または任意位置）について、右母線の右側に注記・コメントを記入できるようにする。
- **現状**: コメント/注記用のモデルは無い。
- **実装方針**:
  - `Sheet` に注記リスト（行番号 or グリッド座標 ＋ テキスト）を追加。
  - `DiagramRenderer` で右母線の右（`RightBusX` + 余白）にテキストを描画。PDF 出力にも反映。
  - ダブルクリックでインライン編集（既存の機器名/枠ラベル編集と同じ仕組みを流用）。
  - 永続化（`.GCAD`）に追加（`GcadSerializer`・スキーマ）。

### 4. 範囲選択・コピー・ペースト
- **内容**: Canvas 上をドラッグして矩形範囲を選択し、Ctrl+C/V でコピー＆ペースト。
- **現状**: 単一要素選択（`_selected`）のみ。複数選択・矩形選択の仕組みは未実装。
- **実装方針**:
  - **範囲選択**: 選択モードでの Canvas ドラッグ（`OnPointerPressed/Moved/Released`）でゴムバンド矩形を描画し、矩形内の `ElementInstance` / `VerticalConnector` / `GroupFrame` をすべて `_selectedSet` に収集。
  - **コピー（Ctrl+C）**: 選択セットを `ClipboardData`（シリアライズ済みの要素リスト＋相対座標）として保持（アプリ内クリップボード）。OS クリップボードは後回しでよい。
  - **ペースト（Ctrl+V）**: ペースト先を「現在のカーソル位置」か「固定オフセット（+1行+1列）」として新規配置。機器名はサフィックス（例: `CR1 → CR1_1`）で自動リネームして重複回避。Undo は `PasteCommand`（複数の `PlaceElementCommand` をバッチ化）。
  - **選択中の描画**: 選択矩形を半透明ハイライト（`FillRectangle` with alpha）で表示、ゴムバンドはダッシュ枠。
  - **留意点**:
    - 縦コネクタはコピー範囲内の要素に挟まれるものだけ含める（端点が両方とも選択範囲内）。
    - 枠（`GroupFrame`）は左上が範囲内なら含める。
    - 機器名の重複チェックは `DocumentModel.AllDeviceNames()` を使う。

### 5. 選択位置への行追加・削除
- **内容**: 現在は行追加（末尾）・行削除（末尾）のみ。選択中の要素が属する行の**前後に行を挿入**、または**その行を削除**する。
- **現状**: `InsertLastRowCommand` / `DeleteLastRowCommand` 実装済み。行番号指定バージョンは未実装。
- **実装方針**:
  - **挿入**: 対象行 `r` の前（または後）に行を挿入 → その行以降の全要素・縦コネクタ・枠の `Row` を +1 シフト。`InsertRowCommand(int targetRow)` を追加。
  - **削除**: 対象行 `r` を削除 → その行の要素を削除後、`Row > r` の要素を -1 シフト。`DeleteRowCommand(int targetRow)` を追加（末尾削除の汎化）。
  - **UI**: ツールバーの「行＋」「行−」ボタンを現在の「末尾操作」から「選択行操作」に変更（未選択時は末尾をフォールバック）。右クリックコンテキストメニューにも追加。
  - **留意点**:
    - 縦コネクタはシフト対象行にまたがる場合 `TopRow`/`BottomRow` を調整。
    - 枠 (`GroupFrame`) は挟まれる行数に応じて `Height` を増減。
    - 挿入対象行の判定: `_selected?.Pos.Row` → 未選択なら末尾。

### 6. 全モードでの空白ドラッグスクロール
- **内容**: 選択モード以外（接点配置モード等）でも、Canvas 空白をつかんでパン（スクロール）できる。現在は選択モードのみ空白クリック → パン開始。
- **現状**: `OnPointerPressed` の `else`（空白クリック）でパン処理をしているが、`_currentTool == ToolKind.Select` のブロック内に限定されている。
- **実装方針**:
  - **Middle ボタンドラッグ**: マウス中ボタン（`PointerUpdateKind.MiddleButtonPressed`）は常時パン開始とする（ツール非依存）。最も副作用が少なく直感的。
  - **全ツールの空白ドラッグ**: `_currentTool != Select` でも、空白（`HitTest` で要素なし）をドラッグしたらパンに切り替える条件分岐を追加。配置モードではプレス離すまで配置を保留し、移動がしきい値（例: 5px）を超えたらパンとみなす。
  - **カーソル**: パン中は `CoreCursorType.Hand` に切り替え（現在の選択モードパンと同様）。
  - **留意点**: タッチ操作（2本指スワイプ）は将来課題。現時点ではマウス操作のみ対応で十分。

### 7. 右クリックコンテキストメニュー（作画モード拡張）
- **内容**: 作画モードでも Canvas を右クリックすると状況に応じたメニューを表示する。
- **現状**: `OnCanvasContextRequested` は**テストモードの接点 ON 強制**のみ実装。作画モードは `if (!_testMode) return` で早期終了している。
- **メニュー構成（コンテキスト別）**:

  | 右クリック対象 | メニュー項目 |
  |---|---|
  | 要素（ElementInstance）を選択中 | 削除 / コメント編集(F2) / 機器名変更 / コピー（実装後） |
  | 縦コネクタを選択中 | 削除 |
  | 枠（GroupFrame）を選択中 | ラベル編集 / 削除 |
  | 空白（何も選択されていない行） | ここに行を挿入 / 末尾に行を追加 |
  | 右母線の右側の空白 | 行コメントを編集 |

- **右クリック取得方法の問題（2026-06-20 調査済み）**: Win2D `CanvasControl` は `WM_RBUTTONDOWN` を Win32 レベルで消費するため `RightTapped` / `ContextRequested` イベントが届かない。WndProc サブクラス化（`WM_CONTEXTMENU` 捕捉）で試みたが x64 バグで未動作。詳細: [rightclick-contact-menu.md](rightclick-contact-menu.md)。
  - **未検証の代替**: `OnPointerPressed` の先頭で `pt.Properties.IsRightButtonPressed` を確認する方法が有効なら最も簡単（WndProc 不要）。**まずこちらを試す。**
  - **WndProc 修正案**: `SetWindowLongW` → `SetWindowLongPtrW`（x64 対応） ＋ `WM_CONTEXTMENU` の `lParam` から画面座標を取得する修正が必要。
- **実装方針**:
  - 右クリック取得が解決したら `if (!_testMode) return` を除去し作画モードの処理を追加。
  - クリック位置で `HitTest` → `HitTestConnector` → `HitTestFrame` の順にヒット判定し、最初にヒットしたものに応じてメニューを切り替え。
  - 既存コマンド（`DeleteElementCommand` 等）を `MenuFlyoutItem.Click` から呼ぶだけなので実装量は小。
  - テストモードの接点メニューは現状維持（既存ロジックをそのまま残す）。

### 8. 枠のヒットテスト精細化
- **内容**: 枠（GroupFrame）の選択ヒット領域が広すぎて、枠内の要素を選択しにくい。また小さい枠同士が重なるとどちらの枠かわからない。
- **現状**: `HitTestFrame` の margin = `_geo.CellMm * 0.35`（セルの 35%）。枠の**外周ボーダー上だけ**をヒットさせる設計だが、margin が大きいため枠内の要素クリック時に枠を拾ってしまうことがある。
- **問題点の詳細**:
  - margin 0.35 セルは描画線幅（0.3mm 程度）に比べて非常に広い。ズームアウト時は許容できるが、等倍・拡大時には不自然に広い。
  - 縦方向上端に `_geo.CellMm * 0.4` のオフセット補正があり、行高とのずれが複合して当たり判定がずれる。
- **改善方針**:
  - margin を `Math.Clamp(_geo.CellMm * 0.15, 2.0, 5.0)` のように**ズームに追従する上限付き**に変更（拡大時は薄く、縮小時も最低 2mm は確保）。
  - ヒット優先順位を明確化: **要素 > 縦コネクタ > 枠外周** とし、枠の内部エリアでは要素クリックが勝つようにする（現状の `OnPointerPressed` は要素を先にチェックしているため、margin を絞れば自然に解決する）。
  - 枠が重なる場合は面積が**小さい枠を優先**（より細かい枠が選ばれる）。`HitTestFrame` 内でヒット候補を面積でソートして最小を返す。
  - 将来的には枠の**四隅・辺中点にリサイズハンドル**を表示し、ドラッグでサイズ変更できるようにする（現在はドラッグ配置のみ）。

### 9. 枠線の線種選択
- **内容**: 枠（GroupFrame）ごとに線種（実線 / 破線 / 点線）を個別に設定できる。
- **現状**: `GroupFrame` モデルに線種プロパティなし。`DiagramRenderer.DrawFrames()` が `_theme.Get(StrokeRole.GroupFrame)` のグローバル設定（常に `LineStyle.Dashed`）を全枠に一律適用している。
- **必要な変更**:
  1. **モデル** (`Element.cs`): `GroupFrame` に `public LineStyle BorderStyle { get; set; } = LineStyle.Dashed;` を追加。
  2. **描画** (`DiagramRenderer.DrawFrames`): `stroke with { Style = f.BorderStyle }` で枠ごとに上書き。
  3. **永続化** (`GcadSerializer`): `.GCAD` の `GroupFrame` に `borderStyle` フィールドを追加（省略時は `Dashed` にフォールバック）。
  4. **UI**: 右クリックコンテキストメニュー（§7）の枠メニューに「線種 →」サブメニュー（実線 / 破線 / 点線）を追加。`SetFrameBorderStyleCommand` で Undo/Redo 対応。
- **留意点**: `Win2DRenderer` / `PdfRenderer` はすでに `LineStyle.Solid/Dashed/Dotted` 対応済みのため、描画側変更は最小限。

### 10. 枠の移動
- **内容**: 選択した枠をドラッグして別のグリッド位置へ移動する。
- **現状**: 枠は選択（`_selectedFrame`）・削除は可能だが移動はできない。`MoveConnectorCommand` に相当するコマンドが枠にはない。
- **実装方針**:
  1. **コマンド** (`ElementCommands.cs`): `MoveFrameCommand(Sheet, GroupFrame, GridPos from, GridPos to)` を追加。
  2. **ドラッグ検出** (`OnPointerPressed/Moved/Released`):
     - 選択モードで `HitTestFrame` ヒット → `_selectedFrame` セット＋`_movingFrame = true`。
     - `OnPointerMoved` でしきい値（5px）を超えたら `TopLeft` をプレビュー更新（描画のみ、コマンド未発行）。
     - `OnPointerReleased` で移動があれば `_history.Execute(new MoveFrameCommand(...))` を実行。
  3. **スナップ**: ドラッグ開始時の枠内クリック位置との相対オフセットを保持し、左上を整数セルにスナップ。
  4. **カーソル**: 枠ボーダーホバー中は `CoreCursorType.SizeAll` に変更。
- **留意点**: 枠内の要素は一緒に移動しない（枠は表示グルーピングのみ）。将来的に「枠＋内部要素の一括移動」オプションを追加検討。

## 追加予定
- UI刷新（GX Works3風）→ 詳細プランは [ui-gxworks3-plan.md](ui-gxworks3-plan.md)
