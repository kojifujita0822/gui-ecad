# MainPage 肥大化 解消プラン

- 対象: `src/GuiEcad.App/MainPage.*.cs`（`MainPage.xaml.cs` 2311 行を中心とする partial 群 計 約 4000 行）
- 目的: コードレビュー指摘 #3（God クラス化）の解消。可読性・変更容易性の改善と、三相記号追加で配置系ロジックが増える前の地ならし。
- 立案日: 2026-06-22
- ステータス: 計画のみ（未実施）。出典レビュー: [code-review-2026-06-22.md](code-review-2026-06-22.md)

---

## 0. 前提と制約（WinUI 固有）

リファクタ方針を縛る制約を先に明確化する。

- **`MainPage` は `Page` 派生のコードビハインド**。XAML（`MainPage.xaml`）と密結合し、`InitializeComponent()` が生成コードと同一クラスを要求する。
  → **コードビハインド本体を「別クラス」に割れない。割れるのは `partial` ファイルへの物理分割まで。**
- **XAML から参照されるイベントハンドラ**（`OnToolSelected`, `OnMenuOpen` 等の `On*`）と `x:Name` フィールドは `MainPage` 内に存在し続ける必要がある。
  → ハンドラ実体を外部クラスへ移す場合も、`MainPage` 側に「薄い転送メソッド」を残す形になる。
- **多数の private フィールドが状態を共有**（`MainPage.xaml.cs:33-175`、数十個）。状態を外部クラスへ移すと参照箇所の一括書き換えが発生し、ここが最大のリスク源。

この制約から、**「ファイル分割（低リスク・形式的）」と「状態抽出（中〜高リスク・本質的）」を別フェーズに分ける**。

---

## 1. 現状の責務マップ（MainPage.xaml.cs 内）

| 行範囲(目安) | 責務クラスタ | 主なメソッド |
|---|---|---|
| 177-200 | 初期化・コンストラクタ | `MainPage()`, 各 Init 呼び出し |
| 201-228 | テーマ | `LoadTheme`, `OnDarkModeToggle`, `OnGridToggle` |
| 249-494 | **パレット ドック/フロート** | `LoadPaletteState`, `SavePaletteState`, `SetPaletteDock`, `SetPaletteOrientation`, `OnPalette*`, `SnapTargetAt`, `UpdateSnapPreview`, `ClampPaletteIntoView`, `LoadToolIconsAsync`, `SvgToImageSourceAsync` |
| 495-651 | **描画（Win2D OnDraw）** | `OnDraw`, `DrawSelection`, `DrawElementHighlight`, `DrawPageGuides`, `SnapLine` |
| 652-878 | ツール/その他パーツ選択 | `OnToolSelected`, `OnOtherPartSelected`, `OpenPartEditor`, `RebuildOtherPartMenu`, `BuildPartSubMenu`, `AddOtherBuiltins`, `OnExport/ImportParts` |
| 879-993 | テストモード・ステータス | `OnTestModeToggle`, `OnTimerTick`, `GetOrCreateTestSession`, `UpdateTestStatus`, `MarkDirty`, `UpdateStatusExtras` |
| 994-1077 | 右パネル/デバイスパネル | `OnRightPanelToggle`, `SetRightPanelVisible`, `AnimateSize`, `RefreshDevicePanel` |
| 1078-1368 | **プロパティパネル** | `ShowPropertiesPanel`, `RefreshPropertiesPanel`, `Commit*`, `AddGeneralAttributes`, `AddLabelPositionSelector`, `On*BoxChanged`, `OnDeviceListSelectionChanged` |
| 1369-1499 | ナビツリー/シート管理 | `RebuildNavTree`, `SwitchToSheet`, `OnAddSheetBtn`, `OnDeleteSheetBtn`, `OnRenameSheetBtn` |
| 1500-1608 | クリップボード | `CopySelection`, `PasteSelection`, `OnCopy/PasteAccelerator` |
| 1609,1776-1876 | **検索/置換** | `UpdateFindResults`, `JumpToFindResult`, `OnFind*`, `OnReplace*`, `CloseFindBar`, `ToggleFindBar` |
| 1616-1722,2004-2039 | 削除・ヒットテスト・座標 | `DeleteSelected`, `HitTest*`, `DistPointToSegment`, `CellEmpty`, `ToWorld` |
| 1723-1775 | キーボード | `OnKeyDown`, `OnKeyUp` |
| 1877-2003 | DRC/出力パネル | `OnRunDrc`, `OnDrcItemSelected`, `RefreshSearchResultPanel`, `OnRunConnectivity`, `OnOutputPanelToggle`, `Collapse/ExpandOutputPanel`, `FormatDiagnostic` |
| 2068-2241 | **コンテキストメニュー** | `ShowDrawingContextMenu`, `ShowContactContextMenu`, `AddItem`, `OnCanvasRightTapped` |
| 2251-2300 | ツールバー/ズーム | `OnFit`, `OnAddColumn`, `OnRemoveColumn`, `OnPointerWheel`, `ZoomBy` |

既存 partial（`Pointer`/`Menu`/`Parts`/`Dialogs`）は責務が概ね揃っており、本プランでは**主に `MainPage.xaml.cs` 本体をほぐす**。

---

## フェーズ1: 機械的 partial 分割（低リスク・即効）

**方針**: ロジックは一切変えず、メソッドを責務ごとの新 `partial` ファイルへ移動するだけ。コンパイル単位・実行時挙動は完全に同一。

### 新規 partial ファイル（案）
| 新ファイル | 移動元の責務クラスタ | 概算行数 |
|---|---|---|
| `MainPage.Palette.cs` | パレット ドック/フロート/アイコン (249-494) | ~250 |
| `MainPage.Drawing.cs` | Win2D `OnDraw` と描画ヘルパー (495-651) | ~160 |
| `MainPage.Tools.cs` | ツール/その他パーツ選択 (652-878) | ~230 |
| `MainPage.Properties.cs` | プロパティパネル (1078-1368) | ~290 |
| `MainPage.Sheets.cs` | ナビツリー/シート管理 (1369-1499) | ~130 |
| `MainPage.Clipboard.cs` | コピペ (1500-1608) | ~110 |
| `MainPage.Find.cs` | 検索/置換 (1609,1776-1876) | ~110 |
| `MainPage.Drc.cs` | DRC/出力パネル/connectivity (1877-2003) | ~130 |
| `MainPage.ContextMenu.cs` | 右クリックメニュー (2068-2241) | ~175 |

**残る `MainPage.xaml.cs` 本体（目標 ~500 行）**: フィールド定義・コンストラクタ・初期化・テーマ・テスト/ステータス・ヒットテスト/座標・キーボード・ツールバー/ズームなど横断的ヘルパー。

### 手順
1. 1 ファイルずつ作成 → 対象メソッドを cut & paste → `dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64` で逐次確認。
2. フィールドはすべて `MainPage.xaml.cs` に残す（partial 間で自然に共有される）。移動しない。
3. 1 ファイル移すごとにコミット（差分が「移動のみ」と確認できる粒度に保つ）。

### 効果と限界
- 効果: 1 ファイルの見通しが劇的に改善。担当責務が明確化。レビュー・grep が容易に。
- 限界: **フィールド共有（密結合）は解消しない**。あくまで「整理」であって「分離」ではない。
- リスク: 低（純粋な移動。ビルドが通れば挙動不変）。

---

## フェーズ2: 状態・ロジックの抽出（中リスク・本質改善）

**方針**: UI 非依存にできる「状態＋純ロジック」を独立クラス（非 `Page`）へ切り出す。`MainPage` は委譲する。

### 候補（独立性が高い順）
1. **`CanvasViewport`** — `_zoom`, `_panX`, `_panY` と `ToWorld`/`ZoomBy`/`OnFit`。純粋な座標変換で UI 依存が薄く、最小リスクで効果あり。単体テスト追加も容易。
2. **`FindController`** — `_findResults`, `_findIndex` と検索/置換ロジック。ドキュメント走査は UI 非依存。`MainPage` は結果ハイライトと UI 更新のみ担当。
3. **`PaletteState`** — ドック状態と `palette-pos.txt` 永続化。座標スナップ判定（`SnapTargetAt`）を含む計算部を分離。
4. **`DrcController` / 出力パネル presenter** — 診断実行と結果整形（`FormatDiagnostic`）。

### 手順
- 抽出クラスは `GuiEcad.App` 内の通常クラスとして新設（テスト可能なものは `Core` 寄せも検討）。
- フィールドを抽出クラスへ移動し、`MainPage` 側の参照を `_viewport.Zoom` 等に一括置換。
- XAML から呼ばれる `On*` ハンドラは `MainPage` に残し、中身を抽出クラスへ転送。

### リスク
- 中。フィールド参照の置換漏れが主因。1 クラスずつ・小さくコミットし、各回 154 テスト＋実機確認。

---

## フェーズ3: 作画ツールのステートマシン化（高リスク・最高価値）

**方針**: レビュー #3 本丸。配置・選択・ドラッグ状態を表すフラグ束を 1 つのツール状態に集約する。

### 現状の問題
配置/操作モードが独立フラグの組合せで表現されている:
`_placeKind`, `_placePartId`, `_placeOrient`, `_placeConnector`, `_placeFrame`, `_moving`, `_movingConnector`, `_panning` ほか。
→ 組合せ爆発・取りこぼし（`ResetDragState`/`ResetPointerState` の「保険」が複数箇所に必要なこと自体が兆候）。

### 目標形
- `enum ToolMode { Select, PlaceElement, PlacePart, PlaceConnector, PlaceFrame, PlaceLine, ... }` ＋必要パラメータを持つ単一の現在ツール状態オブジェクトへ統合。
- もしくは `ITool { OnPressed/OnMoved/OnReleased/OnDraw }` のツールオブジェクト方式（Pointer ハンドラの巨大 `switch`/分岐を各ツールへ分散）。
- `ActivateTool(tag)`（`Parts.cs:285`）を単一の入口に統一。

### リスク
- 高。`MainPage.Pointer.cs`（705 行）の入力処理中核に手が入る。手動操作の回帰が出やすい。
- フェーズ1・2 で土台を整えた後に着手すること。先に [test-plan.md](test-plan.md) の実機チェックリストで操作系の回帰観点を固めてから。

---

## 推奨実行順と検証

| フェーズ | 内容 | リスク | 価値 | 着手条件 |
|---|---|---|---|---|
| 1 | partial 分割 | 低 | 中 | すぐ可 |
| 2 | 状態抽出（Viewport→Find→Palette→Drc） | 中 | 中〜高 | フェーズ1 後 |
| 3 | ツール ステートマシン化 | 高 | 高 | フェーズ1・2 後＋回帰観点整備 |

**各フェーズ共通の検証**:
1. `dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64`（App）
2. `dotnet test GuiEcad.sln`（154 テスト合格を維持）
3. `dotnet run --project src/GuiEcad.App` で実機確認（配置・選択・ドラッグ・コピペ・検索・PDF 出力の主要操作）
4. フェーズ1 は「移動のみ」、フェーズ2/3 は機能単位で小さくコミット。

**注意**: フェーズ1 はリスクが低く効果が大きいため単独でも価値がある。フェーズ2・3 は必須ではなく、保守負荷や次機能（三相記号）の要求に応じて判断する。本プランは「一括大改修」ではなく「段階的・可逆的」な進め方を前提とする。
