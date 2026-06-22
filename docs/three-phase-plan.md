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
