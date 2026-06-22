# コードレビュー報告書 — gui_ecad

- 対象: `src/GuiEcad.Core` / `src/GuiEcad.Pdf` / `src/GuiEcad.App`（生成物 obj/bin を除く実ソース）
- 観点: 整合性 / 可読性 / セキュリティ構成
- 実施日: 2026-06-22
- 備考: 本レビューでは実ビルド・テスト実行は未実施（指摘の挙動確認は `dotnet test GuiEcad.sln` 推奨）。コード変更は行っていない。

---

## 総評
全体として設計が明快で、コメントが丁寧、層分離も良好な健全なコードベース。

- `Core`（UI 非依存のモデル/シミュレーション/描画抽象）→ `Pdf`/`App` の依存方向が正しく、`IRenderer` 抽象で画面と PDF を同一コード化する設計が一貫している。
- `System.Text.Json` 標準・スキーマバージョン検査・冪等な `SeedBasics` など、永続化層の作りは堅実。
- シミュレータ（`Evaluator`）は不動点反復＋周期検出が決定論的に実装され、計算量への言及コメントもある。

---

## 重大（データ損失・移植性）

### 1. ハードコードされた絶対パス — `App.xaml.cs:49`
```csharp
try { System.IO.File.WriteAllText(@"C:\Users\kojif\Desktop\gui_ecad\temp\crash.log", ex.ToString()); }
```
- 開発者個人のデスクトップパス固定。他 PC・配布環境では書込先ディレクトリが存在せず、クラッシュログが残らない。
- 同一機能の他ログ（`MainPage.xaml.cs:2061`）は `Path.GetTempPath()` を使っており方針が不整合。
- 推奨: `Path.Combine(Path.GetTempPath(), "guiecad_crash.log")` 等に統一（`%LOCALAPPDATA%` でも可）。
- 優先度: 高 / 修正コスト: 小

### 2. 未保存変更の破棄確認がない — `MainPage.Menu.cs`（New/Open）＋ ウィンドウクローズ
- `OnMenuNew`（102 行）/`OnMenuOpen`（125 行）とも、`_savedUndoDepth` で Dirty 判定できるのに確認なしで `_document` を差し替える。編集中データが警告なく消える。
- `AppWindow.Closing`/`CloseRequested` ハンドラが見当たらず、×ボタンでも未保存破棄。
- 推奨: Dirty 時に「保存しますか？（保存／破棄／キャンセル）」ダイアログを New・Open・Close 前に挟む。Dirty 判定ロジックは既存のため追加コストは小さい。
- 優先度: 高 / 修正コスト: 中

---

## 中（可読性・保守性）

### 3. `MainPage` の肥大化（God クラス）
- `MainPage` partial が `.xaml.cs`(2311)/`Pointer`(705)/`Menu`(411)/`Parts`(314)/`Dialogs`(321) 計 約 4000 行、フィールドだけで数十個（`MainPage.xaml.cs:33-120`）。View・入力・配置ロジック・ファイル IO・テストセッション管理が一体化。
- partial 分割で緩和されているが、作画状態がフラグ変数の束（`_placeKind`/`_placeConnector`/`_placeFrame`/`_panning`/`_moving` …）で表現されており、状態の組合せ爆発と取りこぼしが起きやすい（「ドラッグ残骸の破棄」保険が複数箇所に必要なこと自体が兆候）。
- 推奨（段階的）: 「現在の作画ツール」を enum ＋ステートオブジェクトに集約し、`Place*`/`_moving`/`_panning` を 1 つのツール状態へ。今すぐの大改修は不要だが、三相記号追加で配置系が増える前に検討する価値あり。
- 優先度: 中 / 修正コスト: 大

### 4. `async void` の多用とエラー処理の二系統
- イベントハンドラの `async void` は WinUI では不可避だが、`ShowErrorAsync(ex.Message)` と `ShowErrorAsync(ex.ToString())`（`OnMenuOpen:157`）が混在し、ユーザ向け表示にスタックトレース全文を出す箇所がある（一般ユーザには冗長）。
- 空 `catch { }` が設定読込/フォルダ作成で多用（`MainPage.xaml.cs:212,225` 他）。意図はコメントで明示され許容範囲だが、`Parts.cs:184` の `_ = ShowErrorAsync(...)`（fire-and-forget）など握り方が場所ごとに違う。方針を 1 つに揃えると読みやすい。
- 優先度: 低〜中 / 修正コスト: 小

### 5. `Params` の文字列ディクショナリ依存
- `elem.Params["Position"]`（`MainPage.xaml.cs:2052`）等、付加情報が `Dictionary<string,string>` のマジックキー＋都度パース。型安全でなくキー名の typo をコンパイルで防げない。
- 推奨: キー名を `const string` 定数に集約（最小コスト）。将来的には用途別の型付きプロパティ化。
- 優先度: 低〜中 / 修正コスト: 小

---

## セキュリティ構成（おおむね良好）

### 良い点
- `System.Text.Json` をデフォルト設定で使用＝ポリモーフィック型解決（旧 `BinaryFormatter`/`TypeNameHandling` 系のデシリアライズガジェット脆弱性）が無効。`.gcad`/`.gcadpart` 読込は安全。
- `PartFolderStore.SafeFileName`（98 行）が `Path.GetInvalidFileNameChars()` でパストラバーサルを抑止。保存先も `Path.Combine` 固定。
- ファイル選択は OS ピッカー経由でパスを取得しており、任意パス注入の余地が小さい。

### 留意点

#### 6. 開発用の動的スクリプト生成・実行 — `MainPage.Menu.cs:268-280`
- 再起動時に PowerShell を `-ExecutionPolicy Bypass` でテンポラリ `.ps1` を生成・実行。`$proj`/`$exe` はシステム由来で外部入力ではないため実害は低いが、配布版に開発専用の「再ビルドして再起動」機能が残るのは構成上望ましくない。
- 推奨: Release ビルドでは `FindAppProject` 経路を `#if DEBUG` で切る。
- 優先度: 中 / 修正コスト: 小

#### 7. クラッシュログ/デバッグログに例外全文（`AppLog`）
- 機微情報を含む可能性は低いが、配布時はログレベルを絞るか出力先を明示すると良い。
- 優先度: 低〜中 / 修正コスト: 小

---

## 対応優先度サマリ
| # | 指摘 | 優先 | 修正コスト |
|---|---|---|---|
| 1 | App.xaml.cs のハードコードパス | 高 | 小 |
| 2 | New/Open/Close の未保存確認 | 高 | 中 |
| 6 | 開発用リビルド機能を Release から除外 | 中 | 小 |
| 3 | MainPage の状態管理リファクタ | 中 | 大 |
| 4,5,7 | エラー処理統一・Params 定数化・ログ方針 | 低〜中 | 小 |

`#1` と `#6` は局所修正で済み、`#2` はデータ損失防止に直結する。着手するならこの順を推奨。
