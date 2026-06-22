# 未決定事項 / TODO

> **2026-06-22 更新**: 154テスト合格。PDF複数ページ分割・A4縦枠・表題欄右下・CR専用ページ・DRC拡張（接点数/二重コイル）・分岐線コピペ等を実装（commit b5156bf）。
> 以下は**未着手・保留**のみ（完了項目は末尾「完了済み（参照用）」と各 commit を参照）。
> **次セッション**: 三相動力回路（主回路図）の作成 → [docs/three-phase-plan.md](three-phase-plan.md) を参照。

## 描画・記号
- [x] **記号の最終調整（ラベル重なり）** — 密配置時のラベル重なり対策。修正済み 2026-06-21。（端子台◎は直径0.3セルに確定済み。標準記号側の追加微調整が必要なら別途相談）
- [x] **PDFフォントのゴシック化** — PDF出力が明朝（Yu Mincho）になっていた不具合を修正。`WindowsFontResolver` を `.ttc` 対応化し、Yu Gothic（YuGothR/B.ttc）→ Meiryo → MS Gothic の順で解決。PDFsharp 6.2.4 はカスタムリゾルバ経由の `.ttc` を直接読めず `OpenTypeFontFace` で NRE になるため、TTC 先頭フォントを単一 sfnt へ再構成して埋め込む（`ExtractSingleFont`）。埋め込みフォントが `Yu Gothic Regular`/`Yu Gothic,Bold` になることを確認。完了 2026-06-21。

## 作図・データモデル
- [x] **部品リスト（BOM）UI** — 「図面(D)→部品リスト(BOM)」。図面から機器名を自動列挙し型式/メーカー/数量を編集（`OnBomEditor`）。`Device` に `Model`/`Maker`/`Quantity` を追加し `DeviceTable.ByName` に永続化。完了 2026-06-21。
- [x] **部品リスト（BOM）の PDF 出力** — BOM 専用ページを PDF 出力の末尾に追加。完了 2026-06-21（commit 8b07879）。
- [x] **新規シートの母線名デフォルト** — 新規シートは `Settings.DefaultBus` を使用。シート設定ダイアログに「この母線名を新規シートの既定にする」チェック追加。完了 2026-06-21。

## 自作パーツ
- [x] **編集スナップの細分化** — `PartEditorWindow` のスナップを 1/4→**1/16セル**（`SnapStep`）に変更。作画自由度向上。完了 2026-06-21。
- [x] **円弧の扁平率（楕円弧）対応** — `PartArc` に縦半径 `Ry`（省略時=真円・後方互換）を追加。作成は外接矩形ドラッグ（横=2rx・縦=2ry、上下方向で弧の向き）、編集はフッタの「扁平率 ry/rx」NumberBox（Undo対応・スナップなしの連続値）。本体/PDF はポリライン近似、エディタは Win2D `AddArc` で楕円描画。完了 2026-06-21。
- [x] **図形の回転（15度スナップ）** — ツールバーに「回転」ツール追加。選択図形を中心まわりにドラッグ回転（15度刻み）。線/折れ線は座標へ焼き込み、矩形/円弧は `Rot` フィールド保持（`PartRect.Rot`・`PartArc.Rot`、省略時0で後方互換）。円は不変・文字は対象外。エディタ/本体/PDF・ヒットテスト（逆回転判定）対応。完了 2026-06-21。
- [x] **パーツライブラリの外部ファイル運用** — `.gcadparts`（ライブラリ）/.`gcadpart`（単体）形式でエクスポート/インポート。「図形(G)→ライブラリのエクスポート/インポート」メニューを実装。複数プロジェクト間でパーツ共有可能。完了 2026-06-21。
- [x] **パレット編集UI** — 既存自作パーツの再編集。`OnEditPart`（MainPage.xaml.cs:333）で「その他▼→パーツ名→編集...」から `PartEditorWindow` を再利用して編集可能。完了済み（実装確認 2026-06-21）。

## UI
- [x] **P3: 下部出力パネル** — DRC/検索結果/接続検査の結果をタブ表示する下部ドック。TabView（折りたたみボタン付き）。完了 2026-06-20。
- [x] **プロパティパネル（基本）** — SelectSwitch ノッチ位置（`Params["Position"]`）・Timer 設定時間（`Params["Setpoint"]`）を `NumberBox` で編集。`SetParamCommand` で Undo/Redo 対応。完了 2026-06-20。
- [x] **プロパティパネル拡張** — 機器名（`RenameDeviceCommand`）・コメント（`SetCommentCommand`）を全要素共通の編集欄として追加。`AddGeneralAttributes` で TextBox 表示、LostFocus/Enter でコミット・Undo/Redo 対応。完了 2026-06-21。
- [x] **ライト/ダークテーマ** — `App.xaml` の ThemeDictionaries ＋ `RequestedTheme` 切替。各パネルを階調化しステータスバーをアクセント帯に。表示メニュー「ダークモード」、設定を `MyDocuments\GuiEcad\ui-theme.txt` に保存・起動時復元。完了 2026-06-21（commit 2f72354）。
- [x] **ヘルプ「使い方」・バージョン1.0.0・起動時空シート** — ヘルプに操作ガイド追加、バージョンを csproj 一元管理＋動的取得、起動時のサンプル回路を廃止。完了 2026-06-21（commit 8922829）。

## 操作機能（追加候補）
（2026-06-21 調査: 以下はすべてコード実装済み。実機動作確認を行うこと）
- [x] **範囲選択・コピー・ペースト** — 「選択」ツールで空白からドラッグ＝範囲選択（青破線）。Ctrl+C／Ctrl+V（マウス位置へ貼付・機器名はコピー元と同じまま）。右クリックメニューからもコピー／貼り付け可能。完了 2026-06-22。
- [x] **選択位置への行追加・削除** — `InsertRowCommand` / `DeleteRowCommand` (ElementCommands.cs L259-370)、右クリックメニューからも操作可能
- [x] **全モードの空白ドラッグスクロール** — スペースキーパン `_spacePanActive` (MainPage.xaml.cs L96, 1417-)
- [x] **右クリックコンテキストメニュー（作画モード）** — `ShowDrawingContextMenu` (L2293-)、`OnCanvasRightTapped` で統一
- [x] **枠のヒットテスト精細化** — `HitTestFrame` margin = `Min(CellMm*0.15, 3.0)` (L1368)、面積最小優先
- [x] **枠線の線種選択** — `GroupFrame.BorderStyle`・`SetFrameBorderStyleCommand`、右クリックサブメニューあり
- [x] **枠の移動** — `MoveFrameCommand`・`_movingFrame` ドラッグ (L1599-1777)
- [x] **枠の自由作成（スナップ解除）** — 作成時にグリッドへスナップせず mm 連続座標で配置。`GroupFrame.VisualWidthMm/HeightMm` 追加、作成は `_frameStartMm/_frameCurMm` で追跡。描画/ヒットテスト/選択ハイライト/プレビューを Visual* 優先に統一。完了 2026-06-21。
- [x] **ウィンドウタイトルにファイル名表示** — `MainWindow.SetDocumentTitle`、`UpdateStatusExtras` から呼出。"&lt;ファイル名&gt;[*] - GuiEcad"。完了 2026-06-21。
- [x] **行番号表示** — `DiagramRenderer.DrawRowNumbers`（左母線左側・1始まり・画面/PDF共通）。`RenderOptions.ShowRowNumbers`（既定 true）。完了 2026-06-21。※従来「行番号は不採用」の設計判断を変更。

## 三相動力回路（主回路図）
制御回路(単線ラダー)とは別構造。多様(3極/2極/インバータ/計器)でグリッド自動配線に乗らないため**自由配線方式**（記号＋自由直線・シミュレーション不要）。参照 `temp/SAMPLE.png`（実図面）。
**→ 作成プラン: [docs/three-phase-plan.md](three-phase-plan.md)（次セッションはここから着手）**
- [x] **自由直線ツール** — 縦パレット「直線」。2点ドラッグ・1/4セル格子スナップ・選択/移動/削除/Undo（`FreeLine`・Place/Delete/MoveFreeLineCommand）。完了 2026-06-21（commit 64dc138）。
- [x] **主回路モード** — シート設定で「主回路（動力回路）」ON＝左右母線・母線名・自動横配線を描かず自由直線で結線（`Sheet.MainCircuit`）。完了 2026-06-21。
- [x] **グリッド表示** — 表示メニュー「グリッド表示」で薄い格子線（`RenderOptions.ShowGrid`）。完了 2026-06-21。
- [x] **ツールパレットのフロート化（Jw_cad 風）** — ドック⇄フロート切替・タイトルバードラッグ移動・位置永続化（`MyDocuments\GuiEcad\palette-pos.txt`）を実装。オーバーレイ `ToolOverlay`（Background=`{x:Null}`）上の `ToolPaletteFloat` に中身（`ToolScroll`）を付け替え。既定はフロート（作図エリア左上 Left=4,Top=4）。**上下端へドラッグ吸着で横並び化**（`ToolTopDock`/`ToolBottomDock` がキャンバスを押し下げ・`SetPaletteOrientation` で縦横変換、enum `PaletteDock` で4状態管理）。完了 2026-06-22。→ [docs/float-palette-plan.md](float-palette-plan.md)。
- [x] **主回路用記号** — ブレーカ(NFB/MCCB/ELB は `Breaker3P`＋`Params["Type"]` でラベル切替)・電磁接触器主接点(`ContactorMain3P`)・サーマル(`ThermalOverload3P` 3極)を `ElementKind` 追加（非シミュレート）。縦/横専用の別記号・接続点「点(●)」ツール・配置プレビュー・PDF整合まで実装。三相モータは既存 Motor、母線R/S/Tは自由直線。完了 2026-06-22（commit cee3a41）。残は記号意匠の微調整等（任意）。→ [docs/three-phase-plan.md](three-phase-plan.md)。

## アプリ全般
- [ ] **パッケージング・配布** — MSIX 等。開発完成後に検討。

---

## 完了済み（参照用）

| カテゴリ | 内容 |
|---|---|
| 描画 | IRenderer/PDFsharp/Win2D/DiagramRenderer/記号全面作り直し/クロスリファレンス表/表題欄 |
| 作図 | 複数シート/PDF全シート出力/Undo-Redo/接続検査UI/線番採番/回路番号採番/縦コネクタUI/機器名入力 |
| データ | 全データモデル/ネットリスト/PortDef/NetlistBuilder/NetlistBuilder PositionToNet |
| 設置場所 | GroupFrame モデル・UI（ドラッグ配置・ラベル編集・削除） |
| テストモード | TestSession/評価エンジン/発振検出/DRC P1-P3/P5-P8 |
| 自作パーツ | PartDefinition/PartLibrary/PartResolver/PartDrawing/PartEditorWindow GUI |
| UI全般 | 機器表パネル/母線名設定/グリッドサイズ設定/ドキュメント情報ダイアログ/検索バー |
| GX Works3 UI刷新 | 縦ツールパレット(P1)/ナビゲーションツリー・TabView廃止(P2)/下部出力パネル(P3)/右パネルタブ化(P4)/標準ツールバー(P5)/ステータスバー強化(P6) |
| 操作機能 | 機器コメント(F2)/行コメント(右母線右側)/行追加削除(Ctrl+Shift+↑↓)/縦コネクタ移動/図面枠(A4・改定欄・ページ番号) |
| 操作機能（session-2026-06-20b） | 範囲選択・コピペ(Ctrl+C/V)/選択行への行挿入・削除/スペースキーパン/作画モード右クリックメニュー/枠ヒットテスト精細化/枠線種(Solid/Dashed/Dotted)/枠ドラッグ移動 |
| テストモード修正 | NetlistBuilder 末尾 Passthrough 自動接続（端子台でコイルが通電されないバグ修正） |
| プロパティパネル | SelectSwitch ノッチ位置・Timer 設定時間の Params 編集 UI（SetParamCommand/Undo対応） |
| ユーティリティ | ヘルプメニュー「再起動」ボタン・端子台サイズ直径 0.3 セル確定 |
| 不具合修正（2026-06-22） | 範囲選択コピペ改善（マウス位置貼付・機器名維持・右クリック対応）／枠移動時の null アンラップ クラッシュ／スペースパン後に選択不能になる不具合／ツールボタンの VisualState 競合（選択中ホバーで選択解除に見える）。XAML VisualState 競合の静的解析テストを追加 |
| PDF/作図（2026-06-22, commit b5156bf） | A4縦の図面枠（既定ON）・表題欄を右下固定・改定欄をその上／長い図面を28行/ページで複数ページ分割（データは母線連続のまま描画のみ分割。`_rowBase`/`PageCount`/`TotalRows`）／クロスリファレンス表を専用ページ化＋「コメント」列追加（コメントは図面に描かず機器単位で集約）／CR番号を図面左の行番号(全行1始まり)に一致＋「ページ-行」形式／上部マージン15→20mm／作画エリアにA4幅×28行のページ境界ガイド |
| 操作/DRC（2026-06-22, commit b5156bf） | 分岐線(縦コネクタ)を範囲選択・コピペ・削除対象に追加／DRC 接点数超過(1コイル5接点以上で警告・最大4)・二重コイル(コイル直列)検出／Ctrl+F を KeyboardAccelerator 化／テキスト入力中の Del/BS で要素が消える不具合修正／ページ番号をリスト順に正規化(「0-1」解消)／行コメントを実効描画行数まで入力可能(8行制限解消) |
