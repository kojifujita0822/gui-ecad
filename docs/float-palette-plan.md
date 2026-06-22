# ツールパレットのフロート化（Jw_cad 風）プラン

> 作成 2026-06-22。三相動力回路（[three-phase-plan.md](three-phase-plan.md)）とは**独立**した別フェーズ。着手タイミングは任意。
> 関連: [todo.md](todo.md) の「三相動力回路」節、CLAUDE.md「UI レイアウト」。
>
> **【実装完了 2026-06-22】** フェーズ1（フロート化）＋フェーズ2の「位置永続化」「ドック⇄フロート切替」＋**上下端への吸着（横並び化）**を実装。
> 仕様確定: ドック⇄フロート切替あり／既定はフロート（作図エリア左上 Left=4,Top=4）／状態・位置を `palette-pos.txt` に永続化。
> **上下端吸着**: フロート中にタイトルバーをドラッグし、作図エリアの上端／下端から `PaletteSnapThreshold=40px` 以内でドロップすると、その端の専用帯（`ToolTopDock`／`ToolBottomDock`、キャンバスを押し下げる Grid 行）へ吸着し、ツールが**横並び**になる（`SetPaletteOrientation` で `StackPanel.Orientation` と区切り線の縦横、横スクロールを切替）。横帯の「切り離す」ボタンでフロートに戻る。
> ドック状態は enum `PaletteDock {Left=0, Float=1, Top=2, Bottom=3}` で管理（永続化数値と一致・旧 0/1 と後方互換）。
> **画面外防止**: ドラッグ時とウィンドウ/パネルのリサイズ時に作図エリア内へクランプ（`ClampPaletteIntoView`／`OnToolOverlaySizeChanged`）。右パネルの裏へ潜り込んで消える不具合を解消。
> **吸着プレビュー**: ドラッグ中、吸着圏内に入ると吸着先を半透明帯 `ToolSnapPreview`（アクセント色・ポインタ透過）で表示（`SnapTargetAt`／`UpdateSnapPreview`）。
> 残（未実装・任意）: 左端へのドラッグ吸着。

## 1. ゴール
縦ツールパレットを、タイトルバーのドラッグで**自由に移動できるフロートパネル**にする（Jw_cad のツールバー風）。主回路は自由配置が多く、パレットを作図エリア上の好きな位置に置けると便利。

## 2. 現状
- パレットは `MainPage.xaml` の `MainAreaGrid` Col0・RowSpan3 に固定（`Border` > `ScrollViewer` > `StackPanel x:Name="ToolStackPanel"` Width=64）。
- ドラッグ用の C# ハンドラは **2026-06-22 にコメントアウトで退避中**（`MainPage.xaml.cs` の「フロートツールパレット」節）。
  - `_paletteDragging` / `_paletteDragOffset`
  - `OnPalettePressed`：`e.GetCurrentPoint(ToolOverlay)` で開始位置取得、`Canvas.GetLeft/GetTop(ToolPaletteFloat)` で現在位置、`ToolPaletteHandle.CapturePointer`
  - `OnPaletteMoved`：`Canvas.SetLeft/SetTop(ToolPaletteFloat, ...)`（`Math.Max(0, ...)` で左上クランプ済み）
  - `OnPaletteReleased`：キャプチャ解放
- 退避理由：参照する XAML 要素 `ToolOverlay` / `ToolPaletteFloat` / `ToolPaletteHandle` が未定義でビルドが壊れたため。

## 3. 実装手順

### フェーズ 1: フロート化（最小実装）
1. **オーバーレイ層を追加**：作図エリア（およびその周辺）に重なる `Canvas x:Name="ToolOverlay"` を配置する。
   - 置き場所候補: `RootGrid` 直下の最前面、または `MainAreaGrid` の作図エリア（Col2 Row1）に重ねる。
   - **重要（落とし穴）**: `ToolOverlay` の `Background` は **`{x:Null}`** にする（`Transparent` だと空き領域でポインタを奪い、下の Win2D キャンバスがクリックできなくなる）。子要素（パレット）の上だけ反応させる。
2. **フロート用コンテナ**：`Border x:Name="ToolPaletteFloat"` を `ToolOverlay` の子に置き、`Canvas.Left`/`Canvas.Top` で初期位置を与える（例: Left=4, Top=4）。
   - 中身: 先頭に**タイトルバー** `Border x:Name="ToolPaletteHandle"`（ドラッグ用・高さ ~16px・ラベル「ツール」）＋ 既存の `ScrollViewer > ToolStackPanel`。
   - タイトルバーに `PointerPressed="OnPalettePressed"` `PointerMoved="OnPaletteMoved"` `PointerReleased="OnPaletteReleased"` を配線。
3. **既存の固定パレット（Col0 の Border）を撤去**し、中身（ToolStackPanel）を `ToolPaletteFloat` に移す。Col0 の `ColumnDefinition` は削除またはゼロ幅化。
4. **退避ハンドラのコメントを外す**（`MainPage.xaml.cs`）。XAML 要素名が揃えば参照解決する。
5. ビルド（`dotnet build -r win-x64`）→ 実機でタイトルバードラッグで移動できることを確認。

### フェーズ 2: 仕上げ（任意）
- **位置の永続化**: パレット位置を `MyDocuments\GuiEcad\palette-pos.txt`（テーマ設定と同様）に保存し起動時復元。
- **ドック/フロート切替**: タイトルバーのボタンで左ドックに戻す/切り離すトグル。
- **画面外防止**: ウィンドウリサイズ時に画面内へクランプ（現状は左上のみクランプ）。
- **ダークテーマ追従**: タイトルバー/枠の色をテーマブラシ（`AppToolbarBackgroundBrush` 等）で。

## 4. 触る主なファイル
- `src/GuiEcad.App/MainPage.xaml`（`ToolOverlay`/`ToolPaletteFloat`/`ToolPaletteHandle` の追加、Col0 撤去）
- `src/GuiEcad.App/MainPage.xaml.cs`（退避ハンドラのコメント解除）

## 5. 要判断（着手時にユーザー確認）
- 完全フロート専用にするか、**ドック⇄フロート切替**を持たせるか。
- 初期位置（左上固定か、従来のドック位置か）。
- 位置の永続化が要るか。

## 6. 完了の定義（DoD）
- タイトルバーをドラッグしてパレットを自由に移動でき、移動中も作図エリアのクリックが阻害されない。
- ビルド 0 エラー、既存テストが緑のまま（UI 変更のためテストは増やさなくてよい）。
