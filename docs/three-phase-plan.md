# 三相動力回路（主回路図）作成プラン

> 作成 2026-06-22。次セッションはここから着手する。正典は本ファイル。
> 関連: [todo.md](todo.md) の「三相動力回路」節、[data-model.md](data-model.md)、[rendering.md](rendering.md)。
> 参照実図面: `temp/SAMPLE.png`（分岐ドット●・端子台◎・主回路の例）。

## 0. 進捗（2026-06-22・三相動力 一区切り＝OK／未コミット）
本セッションで実機確認しながら以下を完了。ユーザー「とりあえず三相動力はOK」。
- **記号を sample.png（実図面）準拠で確定**：NFB/ブレーカ=端子○＋弧∩＋連動縦線、MS/接触器=`─┤ ├─`、OL/サーマル=**2極**（外側2線にZ字段ヒータ・中央素通り）。モータ=⊘端子3＋大円。
- **縦専用・横専用の別記号化（向き切替は廃止）**：メニューはタグ `Kind#V/#H` で配置時に向き確定（計6項目）。`Params["Orient"]` を配置時に付与（`_placeOrient`）。
- **サイズ 2×2 セル**・**極間隔=グリッドピッチ（1セル）**でグリッド線・母線に整合。
- **Motor の端子を1セル間隔（y=-1/0/1）に修正**しズレ解消。
- **接続点「点(●)」ツール追加**（`ConnectionDot`/`Sheet.ConnectionDots`・配置/選択/削除/Undo/永続/PDF）。
- **自由直線を範囲選択コピペ・削除の対象に追加**（mm相対でクリップボード化）。
- **配置プレビュー（マウス追従ゴースト）**：`DiagramRenderer.DrawPreview`。有効列範囲内のみ表示＝範囲が視認可能。
- **グリッド水平線を行中心（行番号位置）に整列**。
- 全164テスト合格・App/Core 0警告0エラー。
- 残（必要なら）：記号意匠の微調整、点を範囲移動対象に含める、主回路PDFの mm ベース分割。
  → **2026-07-01 すべて対応・実機確認完了**（詳細は §8）。記号意匠は現行仕様のままで適合済み（追加調整不要と確認）。点の範囲移動対象化・主回路PDFのmm分割は実装・テスト・実機確認済み。

## （旧）進捗（2026-06-22 更新・フェーズ A/B/C ほぼ完了）
- **フェーズ A 完了**（Core 記号＋テスト）。
  - `ElementKind` 追加: `Breaker3P` / `ContactorMain3P` / `ThermalOverload3P`（3セル幅・空ポート・非シミュレート）。
  - `SymbolGlyphs` に縦流れ3極記号を実装（ブレーカ=刃＋トリップ片＋連動破線、接触器=a接点＋固定接点半円、サーマル=ヒータ矩形＋連動バー）。
  - `Breaker3P` は `Params["Type"]`=NFB/MCCB/ELB で記号右にラベル、ELB はテストボタン印を付加（`DiagramRenderer` でラベル描画・`SymbolGlyphs.Draw` に `variant` 追加）。
  - `PartResolver.BoundarySpan` を空ポートでも安全に（`ports[0]` 参照クラッシュの潜在バグ修正・ボーイスカウト）。
- **フェーズ B 完了**（UI・本セッションで一括実装。peer 連携は中止し当セッションで MainPage を編集）。
  - `MainPage.xaml.cs` の `OtherBuiltins` に「ブレーカ(NFB/MCCB/ELB)」「電磁接触器 主接点(3P)」「サーマル(OL) 3極」を追加（左パレット「その他▼」＋メニューバー「図形」両方に反映）。配置経路は既存 `_placeKind` に自動で乗り `CellWidth=3` 自動適用。
  - ブレーカ型切替（**案B**）: `RefreshPropertiesPanel` に `Breaker3P` 分岐＋ ComboBox（NFB/MCCB/ELB）。コミットは `CommitBreakerType`→`SetParamCommand`（Undo対応）。`DeviceKindLabel` に3記号のラベル追加。
- **フェーズ C（PDF整合）対応済み**: 主回路の自由直線が複数ページ分割時に余白・表題欄へはみ出す問題を、`DrawFreeLines` をページ行ウィンドウへ縦クリップして解消（単一ページは `partialPage=false` で無変更）。
- テスト計11件追加（`NewPartsTests.cs`）。**全164テスト合格**。App/Core ともビルド0警告0エラー。
- **残**: 実機確認（主回路を実際に作図しPDF出力）、コミット（指示待ち）。主回路の主接点向きや記号意匠の微調整は実機確認後に。

### 決定事項（ユーザー確認済み 2026-06-22）
- ブレーカ粒度: **1つの `Breaker3P`＋`Params["Type"]` でラベル切替**（NFB/MCCB/ELB を別 Kind にしない）。
- 接触器主接点・サーマル: **専用3P記号**（`ContactorMain3P` / `ThermalOverload3P`）を新規作成。

## 1. 方針（確定済み・変更しない）
- 主回路は制御回路（単線ラダー）と**別構造**。3極/2極/インバータ/計器など多様でグリッド自動配線に乗らないため、**自由配線方式**（記号＋自由直線、**シミュレーション非対象**）。
- 母線 R/S/T は**自由直線**（`FreeLine`）で縦3本を引く。主回路記号はその3本に重ねて配置する。
- 主回路モード（`Sheet.MainCircuit = true`）は既に左右母線・母線名・自動横配線を描かない。グリッド表示（薄い格子）と自由直線ツールも実装済み。

## 2. 既に出来ていること（前提）
- 自由直線ツール（`FreeLine` / Place・Delete・MoveFreeLineCommand、1/4セルスナップ）
- 主回路モード（`Sheet.MainCircuit`）、グリッド表示（`RenderOptions.ShowGrid`）
- 三相モータ記号 `ElementKind.Motor`（3セル幅・多端子・非シミュレート）

## 3. 作るもの（主回路用記号）
すべて**非シミュレート**（ネットリストに出さない）。実機器名・コメントは付けられる。

| 記号 | 内容 | 端子 | 備考 |
|---|---|---|---|
| NFB（配線用遮断器） | 3極ブレーカ | R/S/T 各上下 | MCCB と同義で扱うか分けるか要判断（下記5） |
| MCCB | 3極ブレーカ | 同上 | |
| ELB（漏電遮断器） | 3極＋漏電 | 同上 | テストボタン印付き |
| 電磁接触器 主接点(3P) | MC 主接点 | 3極 | 既存 `ContactNO` の3連でも可だが専用記号が見やすい |
| サーマルリレー(3P / OL) | 3極過負荷 | 3極 | 既存 `ThermalOverload`(2端子) の3連版 |
| （任意）ヒューズ/計器(A・V)/インバータ枠 | | | フェーズ2以降 |

母線 R/S/T・分岐は自由直線で描く（記号化しない）。

## 4. 実装手順（フェーズ）

### フェーズ A: データモデル & 記号描画（コア）
1. `src/GuiEcad.Core/Model/Element.cs` の `ElementKind` に追加:
   `Breaker3P`（NFB/MCCB兼用）, `EarthLeakageBreaker3P`, `ContactorMain3P`, `ThermalOverload3P`（命名は要確定）。
2. `src/GuiEcad.Core/Model/ElementCatalog.cs`:
   - `DefaultCellWidth`: 3極記号は幅3セル想定（Motor と同様）。
   - `Ports`: 主回路記号は非シミュレートのため**ポート不要**（空）。
   - `CreatesComponent` が **false** を返すことを保証（`IsContact`/`IsLoad`/`IsPassthrough` のいずれにも含めない）。→ ネットリスト・DRC・テストに影響しない。
3. `src/GuiEcad.Core/Rendering/SymbolGlyphs.cs`:
   - `Draw` のディスパッチに `case` 追加、各記号の描画メソッドを実装。
   - 3極記号は3本の縦線（R/S/T 間隔＝1セル）にまたがる横長記号として描く。既存 `Motor`(L210) と `Thermal` を参考に。
4. テスト: `tests/GuiEcad.Tests/` に「新 Kind が `CreatesComponent=false`／ネットリストに出ない／PDF描画で例外が出ない」を追加（`NewPartsTests.cs` のパターン流用）。

### フェーズ B: ツールパレット連携（UI）
1. `MainPage.xaml` の `ToolStackPanel` に主回路記号ボタンを追加（Tag=ElementKind名）。
   - **主回路モードのときだけ表示**するのが望ましい（`Sheet.MainCircuit` で可視切替）。最小実装では常時表示でも可。
2. アイコン: `LoadToolIconsAsync`（MainPage.xaml.cs）に SVG 生成を追加するか、簡易にテキストラベルでも可。
3. 配置は既存の `_placeKind` 経路に乗る（`ActivateTool`/`OnToolSelected` が `Enum.TryParse<ElementKind>` で拾う）。新 Kind は自動的に配置可能になるはず。要確認。
4. 幅3セルの記号は配置時に `CellWidth` を `DefaultCellWidth` から取得済み（`OnPointerPressed` の配置分岐）。占有セル衝突判定（`CellEmpty`）が3セル幅で正しく効くか確認。

### フェーズ C: 仕上げ
1. PDF出力・複数ページ分割との整合確認（主回路シートは行ベースでないため、`MainCircuit` 時の分割挙動を要確認。現状の28行分割は制御回路前提。主回路は mm 高さでの分割が必要かもしれない → 要検討事項）。
2. サンプル作図で実機確認（`temp/SAMPLE.png` 相当を再現）。

> **ツールパレットのフロート化（Jw_cad 風）は別フェーズ**（三相動力回路とは独立）。
> → 専用プラン: [docs/float-palette-plan.md](float-palette-plan.md)

## 5. 要判断・未確定（次セッション冒頭でユーザーに確認）
- **記号粒度**: NFB と MCCB を別 Kind にするか、1つの「3極ブレーカ」記号でラベル（NFB/MCCB/ELB）を切り替えるか。
- **記号の向き**: 主回路は縦流れ（上→下）。記号は縦配置（端子が上下）になる。既存記号は横流れ前提なので、主回路記号は**縦向き専用**で新規描画する。
- **電磁接触器主接点**: 専用3P記号を作るか、既存 `ContactNO`×3 で代用するか。
- **主回路シートの複数ページ分割**: 行ベース（28行）でなく mm ベースが要るか。フェーズ C で検討。
- **自作パーツで代替可能か**: `PartEditorWindow` で主回路記号をユーザー作成できる。標準記号として組み込むか、自作パーツのサンプルライブラリ(`.gcadparts`)として配るか。

## 6. 触る主なファイル
- `src/GuiEcad.Core/Model/Element.cs`（ElementKind）
- `src/GuiEcad.Core/Model/ElementCatalog.cs`（幅・ポート・分類）
- `src/GuiEcad.Core/Rendering/SymbolGlyphs.cs`（記号描画）
- `src/GuiEcad.App/MainPage.xaml` / `MainPage.Parts.cs` / `MainPage.xaml.cs`（パレット・配置）
- `tests/GuiEcad.Tests/NewPartsTests.cs`（新記号テスト）

## 7. 完了の定義（DoD）
- 主回路モードのシートで、母線R/S/T（自由直線）＋ブレーカ＋接触器主接点＋サーマル＋モータを配置して三相動力回路が作図できる。
- 新記号がネットリスト/DRC/シミュレーションに影響しない（既存154テストが緑のまま、新テスト追加）。
- PDF出力で主回路シートが破綻なく出る。

## 8. 次期実装プラン（2026-07-01 起案・侍／隠密調査を踏まえる）

> **2026-07-01 実装・実機確認完了**。起案時点の内容は下記に残すが、実装は完了済み。
> - 8-1 ConnectionDot 範囲選択対応: 実装完了（`_selectedDotSet`/`_movingDot`/`_groupMoveDotAnchor`/`MoveDotCommand`等）。実装直後の忍者確認で「範囲選択でドラッグしても移動しない」不具合が発覚し侍が修正（`_movingDot`・一括移動起点セットアップ追加、点のみ選択時のUndo未コミットも合わせて修正）。再確認で単体ドラッグ・範囲選択一括移動・Undo/Redo すべて正常動作を確認。`_selectedFrameSet` の一括削除対象外バグも同時修正。
> - 8-2 主回路PDFのmmベース分割: 方針B（`MainCircuitVirtualRows`/`RenderPageCount`、ページ高さ=`RowsPerPage*CellMm`=252mm）で実装完了。隠密のコードレビューで `DrawFrames` のみページクリップが漏れているバグを発見、侍が `PushClip`/`PopClip` 対応を追加。忍者が検証プログラム（`GcadSerializer.Load`→実際の描画パイプライン直接呼び出し）でページ境界を跨ぐ枠・母線の正しい分割を確認。
> - 記号意匠の微調整: 忍者が実機配置・目視確認の結果、現行意匠が確定仕様と完全一致しており追加調整は不要と判明。

### 8-1. ConnectionDot の範囲選択対応

**現状の欠落箇所**（隠密調査）:
| 箇所 | ファイル:行 | 内容 |
|---|---|---|
| 範囲選択確定 | `MainPage.Pointer.cs:460-479` | `_selectedSet`/`_selectedConnectorSet`/`_selectedLineSet`/`_selectedFrameSet` は構築するが Dot 用が無い |
| 一括移動起点記録 | `MainPage.Pointer.cs:245-256` | `_multiMoveOrigins` 系に Dot 用が無い |
| クリップボード | `MainPage.Clipboard.cs` | `ConnectionDot` のコピペ未対応（`ClipboardData` に Dots フィールドが無い） |
| 一括削除 | `MainPage.xaml.cs:481-496` | バッチ削除の条件・cmds に Dot が無い |

**追加で確認できた関連事実**: 同じ一括削除ブロック（`MainPage.xaml.cs:483` の条件、`485-489` の `cmds`）は **`_selectedFrameSet`（枠の範囲選択削除）も対象外**になっている。Dot と同じ場所を触るため、ついでに直すか家老の判断を仰ぐ（最小実行の原則に従い、本プランでは「要判断事項」として明示するに留め、自動では含めない）。

**方針**: 既存の `FreeLine` 対応パターンをそのまま踏襲する（`ConnectionDot` も mm 実座標を持つ点で `FreeLine` と同型）。
- 新規フィールド（`MainPage.xaml.cs` 142-180 付近の選択状態フィールド群に追加）:
  - `private HashSet<ConnectionDot> _selectedDotSet = new();`
  - `private Dictionary<ConnectionDot, (double X, double Y)> _multiMoveDotOrigins = new();`
- `ClearMultiSelection()`: `_selectedDotSet.Clear();` を追加。
- `MainPage.Pointer.cs` 範囲選択確定（`OnPointerReleased` 内）: `FreeLine` と同じ `rxL/rxR/ryT/ryB`（mm 範囲）を使い、点が範囲内に収まるものを `_selectedDotSet` に集める。
- `MainPage.Pointer.cs` 一括移動起点記録（`OnPointerPressed` のグループドラッグ分岐）: `_multiMoveDotOrigins = _selectedDotSet.ToDictionary(d => d, d => (d.XMm, d.YMm));`
- `MainPage.Pointer.cs` 一括移動デルタ適用（`OnPointerMoved` の `_multiMoveOrigins.Count > 0` ブロック）: `FreeLine` と同じ `dxMm`/`dyMm` を使い `dot.XMm = orig.X + dxMm; dot.YMm = orig.Y + dyMm;`
- `MainPage.Clipboard.cs`:
  - `ClipboardData` レコードに `List<ConnectionDot> Dots` を追加。
  - `CopySelection()`: `_selectedDotSet`→単一 `_selectedDot` の順で対象決定（既存パターン踏襲）、`oxMm/oyMm` で相対化して `Dots` に格納。
  - `PasteSelection()`: `bxMm/byMm` で平行移動して `PlaceDotCommand` を `cmds` に追加。貼り付け後は `_selectedDotSet` に設定（既存の `_selectedSet`/`_selectedLineSet` 同様）。
- `MainPage.xaml.cs` `DeleteSelected()`: バッチ削除の条件に `|| _selectedDotSet.Count > 0` を追加し、`cmds` に `_selectedDotSet.Select(d => (IUndoCommand)new DeleteDotCommand(_sheet, d))` を `Concat`。
- `MainPage.Drawing.cs`（任意・UI仕上げ）: 67-84 行付近の選択ハイライトループに `_selectedDotSet` 用の輪描画を追加（`_selectedDot` 単体ハイライトと同じ描き方、132-134 行参照）。

**影響範囲・変更ファイル一覧**:
- `src/GuiEcad.App/MainPage.xaml.cs`（フィールド宣言・`ClearMultiSelection`・`DeleteSelected`）
- `src/GuiEcad.App/MainPage.Pointer.cs`（範囲選択確定・一括移動起点記録・一括移動デルタ適用）
- `src/GuiEcad.App/MainPage.Clipboard.cs`（`ClipboardData`・`CopySelection`・`PasteSelection`）
- `src/GuiEcad.App/MainPage.Drawing.cs`（選択ハイライト・任意）

**テスト方針**: `MainPage` はポインタ操作・WinUI 依存のため既存テストに UI 自動テストの前例が無い（`tests/` は `GuiEcad.Core`/`GuiEcad.Pdf` のみ対象）。本件も同様に**自動テスト対象外**とし、`dotnet build`/`dotnet test`（既存154件が緑のまま）で回帰が無いことを確認した上で、**実機確認**（忍者）で「Dot を含む範囲選択 → 一括移動 → コピペ → 一括削除 → Undo/Redo」の一連動作を確認する。

**概算工数**: 0.5〜1人日（既存 `FreeLine` パターンの横展開が中心。実機確認込み）。

**要判断事項**: `_selectedFrameSet` の一括削除対象外バグを本件と同時に修正するか（同一コード行を触るため低コストだが、家老の指示は Dot 対応のみのため別途確認）。

### 8-2. 主回路シートPDFのmmベース分割

**現状**（隠密調査）: `Sheet.MainCircuit = true` でも `DiagramRenderer.cs:53,64-65` の行ベース分割（`RowsPerPage = 28` 固定・`PageCount` も行数ベース）がそのまま使われる。`GuiEcad.Pdf` 配下に mm ベース分割の実装は無い。主回路は自由直線（`FreeLine`／`ConnectionDot`）が mm 座標で自由に配置されるため、要素のグリッド行（`Pos.Row`）だけを基準にした行分割では実際の図面の縦方向の広がりを正しく捉えられない（母線や結線が要素の行範囲を超えて伸びるケースを想定）。

**方針（要判断・2案）**:
- **方針B（推奨・最小変更）**: 主回路シート専用の新規 API を追加し、`Render()` に mm ベースのページウィンドウ（`pageYStartMm`/`pageYEndMm` 等）を渡せる分岐を増やす。既存の行ベース経路（制御回路シート）は無修正・無リスク。実装コストが低くプロジェクト方針（シンプルさ優先）に合う。
- **方針A（将来案）**: `Render()` 内部を「行ウィンドウ→mm バンド」に統一するリファクタを行い、行ベース・mm ベースの両方を同じ mm バンドから導出する。重複ロジックを減らせるが、本件の範囲を超える既存コードの書き換えを伴うためリスクが上がる。今回は見送り、方針Bで様子を見て将来必要なら検討。

**方針B の実装イメージ**:
1. `DiagramRenderer` に主回路の内容高さ算出を追加:
   ```csharp
   // _rowBase=0 前提（Render 呼び出し前 or 専用インスタンスで呼ぶ）
   public double MainCircuitContentHeightMm(Sheet sheet)
   ```
   要素（グリッド行由来）・`FreeLines`（Y1/Y2Mm）・`ConnectionDots`（YMm）・`Frames`（`VisualYMm ?? グリッド由来` + 高さ）の最大 Y を取る。
2. ページ高さ基準値とページ数:
   ```csharp
   public int MainCircuitPageCount(Sheet sheet)
   ```
   基準値候補: `A4H - 2*MarginMm - TitleBlockH`（≒243mm、表題欄を毎ページ保守的に避ける）、または既存の行ベースと統一感を出すため `RowsPerPage * CellMm`（=252mm）を流用。**実機確認で微調整**が必要（要判断）。
3. `Render()` に主回路用の mm ウィンドウ引数を追加し、`sheet.MainCircuit` のときはこちらを使う。`DrawFreeLines`/`DrawDots` は既に mm バンドでクリップしているため流用しやすい。`DrawConnectors`・要素ループ（`InWindow`）・`DrawRowNumbers` は mm バンドを行範囲に変換（`_geo.RowAt`）して適用する分岐を追加。

**影響範囲・変更ファイル一覧**:
- `src/GuiEcad.Core/Rendering/DiagramRenderer.cs`（`MainCircuitContentHeightMm`/`MainCircuitPageCount` 新設、`Render()` の主回路分岐、`DrawConnectors`/要素ループ/`DrawRowNumbers` の mm ウィンドウ対応）
- `src/GuiEcad.App/MainPage.Menu.cs`（PDF出力: `sheet.MainCircuit` のとき新 API へ分岐）
- `src/GuiEcad.App/PdfPreviewDialog.xaml.cs`（プレビューも同じ分岐が必要。`PreviewPage` レコードに mm ウィンドウ用フィールド追加）
- `src/GuiEcad.App/MainPage.Drawing.cs`（`DrawPageGuides` の画面上ページ境界ガイドも主回路時は mm 境界に合わせる・任意だが整合性のため推奨）

**テスト方針**: `GuiEcad.Core` 側の論理は xUnit でテスト可能（既存 `tests/GuiEcad.Tests/PageSplitTests.cs` と同じ形）。
- `MainCircuitContentHeightMm` が要素・自由直線・接続点・枠の最大値を正しく拾うことを検証する単体テスト。
- `MainCircuitPageCount` の境界値テスト（`PageCount_SplitsByRowsPerPage` と同型の `[Theory]`）。
- 複数ページにまたがる主回路シート（自由直線・接続点を意図的にページ境界をまたぐ位置に配置）を `Render()` してテスト用 `IRenderer`（または既存のテスト方式）で例外が出ないことを確認するスモークテスト（`NewPartsTests.cs` 等のパターン踏襲）。
- 既存154テストの回帰確認（`dotnet test GuiEcad.sln`）。
- 実機確認（忍者）: 長い主回路図面（母線が28行相当を超えるもの）を PDF 出力し、ページ境界での自由直線の途切れ・表題欄との重なりが無いことを目視確認。

**概算工数**: 1.5〜2人日（`Render()` 内部分岐の実装・テスト追加・ページ高さ基準値の実機チューニングを含む）。

**要判断事項**:
- ページ高さ基準値（`A4H - 2*MarginMm - TitleBlockH` か `RowsPerPage * CellMm` か）
- 主回路シートでの `ShowRowNumbers`／`DrawPageGuides` の扱い（mm 分割後も継続表示するか）
- 方針A（統一リファクタ）を将来やるかどうか
