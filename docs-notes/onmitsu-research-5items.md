# 隠密調査ノート: 実装候補5件の着手直前調査（2026-07-01）

> 家老依頼: 侍が1)テンプレート保存に着手中、後続5件(5→7→2→3→6)の事前コード調査。
> 本ファイルは調査結果の詳細。要約は claude-peers 経由で家老へ送信済み。
> 元プラン: `C:\Users\kojif\.claude\plans\luminous-shimmying-tarjan.md`

---

## 5. オートセーブ機能（1〜10分間隔設定可）

### 結論
保存エントリポイント・dirty判定・既存設定ファイルパターンいずれも明確。既存の`DispatcherTimer`パターン（テストモード用）を流用すれば実装障壁は低い。

### 根拠（ファイル:行）
| 項目 | ファイル:行 |
|------|-----------|
| Save署名・実装 | `GcadSerializer.cs:13-18` |
| Ctrl+S→SaveAsync | `MainPage.Menu.cs:42-43, 159-167` |
| SaveAsダイアログ | `MainPage.Menu.cs:170-183` |
| `_currentPath`宣言 | `MainPage.xaml.cs:36` |
| `IsDirty`定義 | `MainPage.xaml.cs:457`（`_history.UndoDepth != _savedUndoDepth`） |
| `_savedUndoDepth`宣言 | `MainPage.xaml.cs:136` |
| `CommandHistory.UndoDepth` | `CommandHistory.cs:12` |
| 新規作成時リセット | `MainPage.Templates.cs:69-91` |
| 読込時リセット | `MainPage.Menu.cs:127-152` |
| palette-pos.txt保存（カンマ区切りテキスト） | `MainPage.Palette.cs:40-42, 79-84` |
| ui-theme.txt保存（"Dark"/"Light"単一行） | `MainPage.xaml.cs:245-247, 271-272` |
| 両者ともMyDocuments\GuiEcad、Directory.CreateDirectory自動作成 | 同上 |
| DispatcherTimer流用元（テストモード用） | `MainPage.xaml.cs:103, 351-354` |

### 要判断事項
1. autosaveファイルの命名規則・保存先（別拡張子か別ディレクトリか）
2. 無題ドキュメント（`_currentPath`がnull）の扱い（オートセーブしない／専用フォルダへ／一時拡張子）
3. オートセーブ中にユーザーがCtrl+Sした場合の競合（キューイングか直列化か）
4. 失敗時のUI通知有無
5. 設定ファイル形式（palette-pos.txt同様のテキストでよいか）、間隔値は整数分/秒どちらで持つか
6. アプリ終了時、未保存オートセーブの扱い（削除／一時保存／復帰prompt）

---

## 7. マウスレス要素配置（キーボードモード）

### 結論
配置経路・既存キー割当ての衝突箇所とも特定済み。**矢印キー（↑↓←→）は現状ハンドラなしで安全に使用可能**。Enterキーは機器名編集起動と衝突するため、モード分岐が必要。

### 根拠（ファイル:行）
| 項目 | ファイル:行 |
|------|-----------|
| `_placeKind`取得 | `MainPage.xaml.cs:46` |
| 配置判定・実行 | `MainPage.Pointer.cs:258-314` |
| 行・列算出 | `MainPage.Pointer.cs:115`、`GridGeometry.RowAt/ColAt`（`GridGeometry.cs:23-26`） |
| 配置コマンド実行 | `MainPage.Pointer.cs:307` |
| Enterキー：機器名編集起動 | `MainPage.Menu.cs:57-62`（検索バー中は除外） |
| Enterキー：インライン編集確定 | `MainPage.Pointer.cs:800, 847, 896, 939` |
| Ctrl+Shift+↑↓：行追加削除 | `MainPage.xaml:101-102` |
| 矢印キー(↑↓←→)単体 | ハンドラなし（衝突なし・安全） |
| enum ToolMode | `MainPage.xaml.cs:34` |
| `_tool`フィールド | `MainPage.xaml.cs:43` |
| F5-F8ショートカット（既存ツール切替） | `MainPage.xaml.cs:613-616` |
| ActivateToolメソッド | `MainPage.Parts.cs:392-408` |
| KeyDownイベント（Page全体、Canvas個別ハンドラなし） | `MainPage.xaml.cs:601` |
| フォーカス確保 | `MainPage.Pointer.cs:95`（`Canvas.Focus(FocusState.Programmatic)`） |
| テキスト編集検出 | `IsInlineEditing`（`MainPage.xaml.cs:119-121`）、`IsTextInputFocused()`（129-133） |
| ホバーセル（初期位置候補） | `_hoverCell`（`MainPage.Pointer.cs:425`） |

### 要判断事項
1. キーボードモード起動トグルの配置（ToolbarBorder内、`MainPage.xaml:171-226`参照）
2. フォーカスセル初期位置（左上固定 or `_hoverCell`再利用）
3. Enterキーの新用途（配置確定 vs キーボードモード終了）— 既存`OnEnterAccelerator`との共存条件を明示
4. 矢印キー実装場所（Page.OnKeyDown追記が安全、テキスト入力中は既存ガードで除外済み）
5. キーボードモード中のマウス無効化 vs 並行使用
6. フォーカスセルの視覚表示（矩形枠＋StatusPos行:列表示への統合）

---

## 2. A3用紙サイズ対応

### 結論
`RowsPerPage`は7ファイル・8箇所から参照されており、単純な定数変更では対応不可。**主回路mm分割ロジック（`MainCircuitVirtualRows`）自体はmm座標依存でRowsPerPage非依存のため、RowsPerPageの値を差し替えるだけで正しく動く**（ロジック変更不要）。ただし`MainPage.Drawing.cs`にA4W=210のローカルハードコードが別途あり、要修正。

### 根拠（ファイル:行）
| 項目 | ファイル:行 |
|------|-----------|
| `RowsPerPage`定義 | `DiagramRenderer.cs:53`（`public const int RowsPerPage = 28`） |
| `PageCount()`（static） | `DiagramRenderer.cs:65` |
| `RenderPageCount()`（instance） | `DiagramRenderer.cs:96` |
| PDF出力でのページ分割 | `MainPage.Menu.cs:261-262`（`p * RowsPerPage`） |
| 画面ページガイド帯 | `MainPage.Drawing.cs:259`（`bandH = rpp * cell`）、**同ファイル258行に`const double a4w=210.0`のローカル重複ハードコードあり** |
| プレビュー用ページ行開始位置 | `PdfPreviewDialog.xaml.cs:72, 216` |
| テストでの28前提 | `PageSplitTests.cs:40, 65, 121, 150, 259` |
| `A4W`/`A4H`定義 | `DiagramRenderer.cs:106-107`（private const） |
| `PageSize()`でA4固定返却 | `DiagramRenderer.cs:116` |
| クロスリファレンス表右端基準 | `DiagramRenderer.cs:604, 623` |
| クロスリファレンス専用ページ | `DiagramRenderer.cs:679, 685` |
| 表題欄右下固定配置 | `DiagramRenderer.cs:935-938` |
| 図枠描画 | `DiagramRenderer.cs:1006` |
| `MainCircuitVirtualRows`（mm座標依存、RowsPerPage非依存を確認） | `DiagramRenderer.cs:70-82` |
| `TitleBlockH=20.0`/`RevRowH=7.0`/`RevHdrH=5.0`（mm単位、用紙サイズ非依存） | `DiagramRenderer.cs`内 |
| `TitleBlockW=95.0`（水平位置、A3では調整要） | `DiagramRenderer.cs`内 |

### 要判断事項（設計方式の選択）
| 判断点 | 選択肢 |
|------|------|
| RowsPerPage実装方式 | (a)静的メソッドに引数追加 (b)`RenderOptions`にPaperSize追加しインスタンス化 (c)DocumentInfoにPaperSizeフィールド |
| A4W/A4H統一 | (1)定数→計算式 (2)`enum PaperSize`で幅高辞書管理 |
| テスト更新 | `PageSplitTests`のRowsPerPage=28前提をパラメータ化 |

**調査エージェント所感（推奨）**: (b)+(2) — `RenderOptions`に`enum PaperSize{A4,A3}`追加、RowsPerPageはPaperSizeからの計算値に。既存呼び出し側（Menu.cs, PreviewDialog.cs）でPaperSize情報を渡す経路を追加。`MainPage.Drawing.cs:258`のローカル`a4w`重複も同時修正必須。

---

## 3. ショートカットキー設定機能

### 結論
XAML `KeyboardAccelerator` と `OnKeyDown` 分岐の混在実装。Undo/Redo・コピペ・削除・行操作は既に`IUndoCommand`パターンで統一されており、ディスパッチテーブル化は構造的に可能。**Escapeキーが7つの異なる意味を持ち、if-else連鎖の順序に依存**しているため、カスタマイズ時は優先順位の明示化が必須。

### 根拠（ファイル:行、キー割当て一覧）

**XAML KeyboardAccelerator（11個）**:
| キー | ファイル:行 | コマンド |
|------|----------|---------|
| Ctrl+Z / Ctrl+Y | `MainPage.Menu.cs:38-41` | Undo/Redo |
| Ctrl+S | `MainPage.Menu.cs:42-43` | Save |
| Delete | `MainPage.Menu.cs:48-54` | 削除（テキスト入力中は委譲） |
| Enter | `MainPage.Menu.cs:57-62` | 機器名編集起動（検索バー中除外） |
| F2 | `MainPage.Menu.cs:64-69` | コメント編集起動 |
| Ctrl+Shift+↑/↓ | `MainPage.Menu.cs:71-86` | 行追加/削除（テストモード中無視） |
| Ctrl+C / Ctrl+V | `MainPage.Clipboard.cs:151-155` | コピー/ペースト（テストモード中無視） |
| Ctrl+F | `MainPage.xaml.cs:484-485` | 検索バー表示切替 |

**OnKeyDown内（`MainPage.xaml.cs:601-639`）**:
BackSpace(608-611)／F5-F8(613-616、ツール切替)／Escape(619-627、7分岐)／Space(630-647、パン)

**インライン編集4TextBox共通（Enter/Tab/Escape）**: DeviceNameBox(`Pointer.cs:847-850`)／FrameLabelBox(800-803)／CommentBox(896-899)／RungCommentBox(939-942)

**検索ボックス**: Enter/Escape（`MainPage.Find.cs:72-73`）

**未実装（メニュー表記のみでKeyboardAccelerator未登録）**: Ctrl+/-/0（ズーム、`MainPage.xaml:144-146`に表記あるがCtrl+ホイールのみ実動作）、Ctrl+N/O

### 要判断事項
1. テキスト入力判定の統一（`IsTextInputFocused()`と`IsInlineEditing()`の並存を`IsEditingOrInputFocused()`等に統一）
2. カスタマイズ不可にすべき予約キー（Escape/Enter/Tab等）の明示
3. Ctrl+/-/0・Ctrl+N/Oを正式実装するか
4. Escapeの優先順位ルールを列挙・明示化（現状はコード順依存）
5. 設定ファイル格納先（`MyDocuments\GuiEcad\keybindings.json`想定）
6. キー競合時の挙動（後優先＋ロード時警告等）
7. テストモード中に制限すべきキーの一覧化

**調査エージェント所感（実装順序案）**: ①未実装キー追加 → ②テキスト入力判定の統一 → ③`KeyBinding{VirtualKey, Modifiers, CommandId}`モデル定義 → ④ディスパッチテーブル化 → ⑤JSON設定I/O → ⑥設定UI

---

## 6. 画像挿入機能（トレース用下絵＋PDF埋め込み恒久貼付の両対応）

### 結論
BMP/PNG画像の挿入は**画面描画・PDF出力とも技術的に実現可能**（Win2D `CanvasBitmap`、PDFsharp `XImage.FromFile`/`XGraphics.DrawImage`）。**PDFファイル自体の埋め込みはPDFsharpでは不可**（PDF作成専用ライブラリで既存PDF読込・ラスタライズ機能なし）。外部ライブラリ追加はCLAUDE.mdの「不要な外部依存を追加しない」方針と相反するため要相談 → **【2026-07-01 決定】人間確認済み: PDFファイル自体の挿入は不要。BMP/PNGのみで確定、外部依存追加なし。**

### 根拠（ファイル:行）
| 項目 | ファイル:行 |
|------|-----------|
| `IRenderer`抽象、現状メソッド一覧（画像描画メソッドなしを確認） | `src/GuiEcad.Core/Rendering/IRenderer.cs`（DrawLine/DrawRectangle/FillCircle/DrawText/DrawArc等10個） |
| Win2D側`CanvasDrawingSession.DrawImage`で実装可能 | `src/GuiEcad.App/Win2DRenderer.cs:153` |
| `CanvasBitmap.LoadAsync`でファイル読込可 | 同上 |
| PDFsharpバージョン | `GuiEcad.Pdf.csproj:8`（6.2.4） |
| `XGraphics.DrawImage`/`XImage.FromFile`でBMP/PNG/JPEG埋め込み可（PDF/GIF/TIFF等は6.2.4で要別途確認） | `src/GuiEcad.Pdf/PdfRenderSurface.cs` |
| `Sheet`モデル現状構成（Elements/Connectors/FreeLines/ConnectionDots等） | `src/GuiEcad.Core/Model/Sheet.cs:26` |

### 提案設計
```csharp
// IRenderer 追加案
void DrawImage(ImageData image, Rect2D bounds, double rotationDeg = 0, double opacity = 1.0);

// Sheet 追加案
public List<ImageInsert> Images { get; set; } = new();
public sealed class ImageInsert {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = "";   // 外部参照 or Base64
    public Rect2D Bounds { get; set; }            // mm座標
    public double RotationDeg { get; set; } = 0;
    public bool IsTracingOnly { get; set; }       // trueならPDF出力除外＝下絵専用
    public double Opacity { get; set; } = 1.0;
}
```
`IsTracingOnly`フラグ一つで「トレース用下絵（画面のみ）」「恒久貼付（PDFにも出力）」の両対応を1モデルで表現可能。永続化はschemaVersion引き上げが必要（`docs/persistence.md`）。

### 要判断事項
1. **PDFファイル自体の埋め込み方針** — **【2026-07-01 決定】人間確認済み: 「PDFファイル自体の挿入は不要（BMP/PNGのみでよい、外部依存追加なし）」。上記案a)で確定。実装は`IRenderer.DrawImage`＋`Sheet.Images`（`ImageInsert.IsTracingOnly`でトレース用/恒久貼付を切替）の設計をBMP/PNG限定でそのまま進めてよい。**
2. 画像ファイルの永続化方法（Base64埋め込み vs 外部ファイル参照＝.GCADと同フォルダに配置してパス保存）— 調査エージェントは外部参照を推奨（シンプルさ重視のCLAUDE.md方針に合致）
3. 対応形式の最終範囲（BMP/PNG確定、JPEG追加するか）
4. 画像サイズ制限（メモリ・UI応答性のため）
5. 描画順序（Elements等より背面固定にするか）
6. スケール基準（画像px→mm自動計算 vs 明示指定のBounds）

---

## 全体を通しての隠密所感

- 5・7・2・3・6のすべてが、着手可能な粒度まで調査完了（6の「PDFファイル自体の埋め込み」論点も2026-07-01に人間確認により解消済み＝BMP/PNGのみで確定）。
- 各項目の細かい「要判断事項」（無題ドキュメントの扱い・設定ファイル形式等）は実装時に侍が妥当な値で決めて差し支えない粒度と判じ申す。
