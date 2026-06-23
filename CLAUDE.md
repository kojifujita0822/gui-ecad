# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **本ファイルは設計の索引です。** 基盤実装・154テスト合格・GUI 一式（作画/テスト/PDF/検索/ナビゲーションツリー/機器表/枠/表題欄/GX Works3風UI/プロパティパネル/コピペ/右クリックメニュー/枠操作/ライト・ダークテーマ/A4縦図面枠/複数ページ分割/クロスリファレンス専用ページ）動作済み。
> **三相動力回路（主回路図）の自由配線作図を実装中**（自由直線ツール・主回路モード・グリッド表示は実装済み。次は主回路用記号 → [docs/three-phase-plan.md](docs/three-phase-plan.md)）。
> 詳細は `docs/` 配下（各 `docs/` が正典）。未着手・保留は [docs/todo.md](docs/todo.md)。
>
> **運用ルール**: 本ファイル（CLAUDE.md）は **200行以内**に収める。超過する内容は `docs/` 配下へ分離し、CLAUDE.md には**要約とリンクのみ**を記載する。

## プロジェクト概要
GUI で入力できるラダー図作成用の **Windows デスクトップアプリケーション**。マウス操作で接点・コイル等を配置・接続して図を編集し、**モード切替**で作画モード（図面作成）とテストモード（動作確認）を切り替える。

**核心方針**: 作図の**記法**はラダー図的（均一グリッド上の記号配置）だが、記入・運用の**流儀は「シーケンス図（展開接続図）」スタイル**。PLCの抽象アドレス方式ではなく、有接点リレーシーケンスの実務（実機器名・設置場所枠・クロスリファレンス）に沿う。→ [docs/drawing-spec.md](docs/drawing-spec.md)

## 技術スタック（確定）
- **言語/UI**: C# / .NET 8、**WinUI 3**（Windows App SDK）
- **画面描画**: **Win2D**（`Microsoft.Graphics.Win2D` / `CanvasControl`）による自前グリッド描画
- **ベクター/PDF 出力**: 必須。**ベクターPDF**を **PDFsharp**（MIT）で出力（PDF専用）
- **描画抽象化**: 画面とPDFを同一コードで両対応する **`IRenderer`** を中核に導入 → [docs/rendering.md](docs/rendering.md)
- **永続化**: JSON 単一ファイル（`System.Text.Json`）、拡張子 **`.GCAD`** → [docs/persistence.md](docs/persistence.md)
- **テスト**: xUnit（154件全合格、既知不具合なし）

## アーキテクチャ要点
- **MVVM**（View=XAML / ViewModel / Model）。
- **データモデルは UI 非依存**。永続化する**ドキュメントモデル**と、幾何から導出する**ネットリスト**（シミュレーション用・非永続）を分離 → [docs/data-model.md](docs/data-model.md)
- **機器（Device）を名前で一元管理**し、状態とクロスリファレンスのキーにする。
- **記号（見た目）と種別（データ）を分離**（端子台記号の差し替え予定に対応）。
- **テストモード**は有接点リレーの **connectivity ＋ 不動点反復**で評価（PLCスキャンではない＝評価順序非依存） → [docs/simulation.md](docs/simulation.md)
- 線番号＝ネット（電気的連続）単位、回路番号＝横の回路線単位、いずれも**自動順送り採番**。データモデル上の「ステップ番号」は不採用だが、左母線左側に**視覚ガイドとしての行番号（1始まり）**を表示する（`RenderOptions.ShowRowNumbers`、2026-06-21 追加）。

## ビルド / 実行 / テスト
構成: `src/GuiEcad.Core`（IRenderer 抽象・Model・Simulation）/ `src/GuiEcad.Pdf`（PDFsharp）/ `src/GuiEcad.App`（WinUI3・Win2D）/ `tests/GuiEcad.Tests`（xUnit）。詳細は [docs/setup.md](docs/setup.md)。
```pwsh
# App のビルド/実行は dotnet run（win-x64 RID ツリー bin\Debug\...\win-x64）に統一する。
#   ※ -p:Platform=x64 は出力先が bin\x64\Debug と別ツリーになり、古いバイナリを実行する事故の元。
#      App 単体ビルドでは使わない（2026-06-21 案A で 1 本化）。実機確認も dotnet run で。
dotnet run --project src/GuiEcad.App
# 明示的にビルドのみ行う場合も RID 指定で同じツリーを使う
dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64
dotnet build GuiEcad.sln          # Core/Pdf/Tests（sln は従来どおり Platform 指定可）
dotnet test  GuiEcad.sln
```

## UI レイアウト（GX Works3 風、2026-06-20 完成）
```
[メニューバー]                                               Row0
[標準TB] テスト|Undo/Redo|削除|接続検査|ズーム|列±|行±|PDF  Row1
┌─────┬──────────┬──────────────────────┬──────────────┐
│縦   │ナビツリー │  Win2D CanvasControl  │ [タブ]機器表 │
│パレ │ シート   │                      │     プロパティ│ Row2
│ット │ ＋ － 名│  （ラダー図作図エリア）│              │
│(64) │ (180px)  │                      │   (220px)    │
└─────┴──────────┴──────────────────────┴──────────────┘
[ステータスバー] 作画/テストモード | 行列 | シート番号 | 変更有無  Row3
```
- **縦ツールパレット (Col0)**: `ToolStackPanel` (ScrollViewer 内 StackPanel)。選択/接点/出力/分岐/枠/その他▼。**ドック⇄フロート＋上下端吸着可**（Jw_cad 風・enum `PaletteDock {Left,Float,Top,Bottom}`）。フロート時は `ToolOverlay`(Canvas, Background=`{x:Null}`) 内 `ToolPaletteFloat` に中身(`ToolScroll`)を付け替え `ToolPaletteHandle` ドラッグで移動。上端/下端から40px以内でドロップすると `ToolTopDock`/`ToolBottomDock`(キャンバスを押し下げる Grid 行) へ吸着し横並び化(`SetPaletteOrientation`)。状態/位置は `MyDocuments\GuiEcad\palette-pos.txt` に永続化。既定はフロート(Left=4,Top=4)。→ [docs/float-palette-plan.md](docs/float-palette-plan.md)
- **ナビゲーションツリー (Col1)**: `NavTree` (TreeView)。`_sheetNodeMap` で Sheet ↔ TreeViewNode を管理。TabView は廃止済み。
- **右パネル (Col3)**: `RightPanelTabView` に「機器表」「プロパティ」タブ。`DevicePanel` がコンテナ（`MainAreaGrid` 内 Col3 Row0/RowSpan2）。プロパティタブ: SelectSwitch ノッチ位置・Timer 設定時間を `NumberBox` で表示・Undo/Redo 対応（`SetParamCommand`）。
- **ステータスバー**: `StatusMode` / `StatusPos` / `StatusSheet` / `StatusDirty` / `StatusWarn`
- Dirty フラグ: `_savedUndoDepth` vs `_history.UndoDepth` で判定。保存/新規/読込時にリセット。
→ 詳細: [docs/ui-gxworks3-plan.md](docs/ui-gxworks3-plan.md)

## ドキュメント索引
| ファイル | 内容 |
|---|---|
| [docs/drawing-spec.md](docs/drawing-spec.md) | 作図仕様（シーケンス図スタイル・番号体系・作画モード入力・PLC対比） |
| [docs/rendering.md](docs/rendering.md) | 描画設計（`IRenderer` API・バックエンド対応・線スタイル/`DrawingTheme`） |
| [docs/data-model.md](docs/data-model.md) | データモデル（ドキュメント／ネットリスト・C#スケッチ） |
| [docs/simulation.md](docs/simulation.md) | テストモード評価アルゴリズム（不動点・発振検出） |
| [docs/persistence.md](docs/persistence.md) | 永続化フォーマット（JSON / `.GCAD`） |
| [docs/setup.md](docs/setup.md) | セットアップ・プロジェクト構成・雛形生成手順 |
| [docs/todo.md](docs/todo.md) | 未着手・保留タスク一覧／進捗 |
| [docs/test-plan.md](docs/test-plan.md) | 総合テストプラン（パッケージング前・自動＋実機チェックリスト） |
| [docs/packaging.md](docs/packaging.md) | パッケージング・配布手順（unpackaged self-contained） |
| [docs/three-phase-plan.md](docs/three-phase-plan.md) | 三相動力回路（主回路図）作成プラン（次セッション着手） |
| [docs/float-palette-plan.md](docs/float-palette-plan.md) | ツールパレットのフロート化プラン（三相と独立の別フェーズ） |

> 完了済みの実装計画書（GX Works3 UI 刷新・操作機能追加・自作パーツ実装・右クリックメニュー）は
> 2026-06-21 に削除した。実装済みのためコードと各正典・[docs/todo.md](docs/todo.md) が現状の正典。
> 履歴が必要なときは git 履歴を参照。
