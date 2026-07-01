# UIフレームワーク技術スペック変更 見積もり調査（2026-07-01・隠密）

> 背景: 枠ラベル早期確定バグ（[docs-notes/keyboard-input-fix-plan.md](keyboard-input-fix-plan.md) ラウンド8）がWinUI3自体の既知バグ（[Issue #6179](https://github.com/microsoft/microsoft-ui-xaml/issues/6179)）由来と判明したことを受け、殿より「実装技術スペックの変更で同程度の機能を導入する場合のコスト」を問われ、見積もり調査を実施。**実装は行っていない。調査結果のみ。**

## 0. 前提規模の訂正（重要）

家老から提示された規模「src/GuiEcad.App＝C# 72ファイル22,271行＋XAML 20ファイル4,572行」を検証のため実測したところ、**この数値は`obj`/`bin`配下の自動生成コード（`.xaml.g.cs`・`XamlTypeInfo.g.cs`等）を含んだものだった**。

実際に人間が書いたソースコードのみを実測した結果:

| 対象 | 提示値（生成物込み） | 実測値（ソースのみ） |
|---|---|---|
| C#ファイル数 | 72 | **30** |
| C#行数 | 22,271 | **8,565** |
| XAMLファイル数 | 20 | **5** |
| XAML行数 | 4,572 | **1,142** |
| 合計 | 約26,800行 | **約9,700行** |

以下の見積もりは実測値（約9,700行）を前提とする。当初想定の半分以下の規模であり、以降の工数見積もりに反映する。`src/GuiEcad.Core`（4,167行、UIフレームワーク非依存）・`src/GuiEcad.Pdf`（PDFsharp、フレームワーク非依存）は影響を受けない前提は変わらず。

## 1. パターンA: WinUI3を別UIフレームワークへ全面置き換え

### 1-1. WinUI3固有API依存度の概観

| カテゴリ | 依存ファイル数 | 中核・規模感 |
|---|---|---|
| Win2D（CanvasControl等） | 27ファイル中で使用 | `Win2DRenderer.cs`(189行・描画中核)、`MainPage.Drawing.cs`(291行)。座標変換(`CanvasViewport.cs`45行)はUI非依存で再利用可 |
| KeyboardAccelerator・フォーカス管理 | 12ファイル | `MainPage.KeyBindings.cs`(296行・動的登録)、`MainPage.KeyboardMode.cs`（Escape8分岐等の複雑な状態機械） |
| WinUI3標準コントロール | 17ファイル | MenuBar・ContentDialog（多重表示ゲート実装）・TabView・RadioButton等 |
| その他WinUI3/UWP固有API | 17ファイル | XamlRoot（ContentDialog/FilePicker必須）・FileSavePicker+GetPickerWindowId・DispatcherQueue・AppWindow |

**MVVM分離度**: 低い（コードビハインド主体）。MainPageのpartial class群にUIイベント処理が直結し、ICommand/ViewModelパターンは未導入。一方、Undo/Redo（Commands/）とモデル層（GuiEcad.Model等）との境界は明確で、この部分は移行時も無傷で再利用できる。

### 1-2. 移行先候補

**候補1: Avalonia UI**
- 2D描画: SkiaSharpベース（`ICustomDrawOperation`/Composition Custom Visual）でWin2D同等以上の描画が可能。Windows上ではDirect2Dも選択肢にあり、Skiaがクロスプラットフォームの既定（[Avalonia Docs](https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering)）。2026時点でAvalonia 12プレビューが新しい合成ベースレンダリングパイプラインを導入し性能向上見込み。
- PDFsharp: .NET Standard対応のフレームワーク非依存ライブラリのため問題なく利用可能。
- フォーカス管理: 独自のFocusManager体系を持つが、今回のような「PointerReleased後の暗黙フォーカス委譲」既知バグの有無は未検証。WinUI3より若いフレームワークのため、**同種の未知の制約が別の形で出るリスクは否定できない**。
- クロスプラットフォーム対応は本アプリには本来不要（Windows専用）でオーバースペックだが、コミュニティが活発でWPF知識の転用がしやすい。

**候補2: WPF (.NET 8)**
- 2D描画: `DrawingContext`/`DrawingVisual`でレイアウト・イベント処理をバイパスした軽量高性能描画エンジンを構築可能。
- PDFsharp: 本家がWPFの`DrawingContext`から直接`XGraphics`を生成する統合をサポート済み（`PdfSharp.Drawing.XGraphics`経由）。**候補中もっとも親和性が高い**。
- フォーカス管理: 20年以上の実績があり枯れている。Issue #6179のような未知のフォーカス系バグに新たに遭遇するリスクはAvaloniaより低いと推測される。
- .NET 8で引き続きサポートされる安定技術だが、新機能追加は停滞気味（保守フェーズ）。最新Fluentデザインとの親和性はWinUI3に劣る。

### 1-3. 概算工数（人日、ラフ）

| 作業項目 | 工数目安 |
|---|---|
| Win2D描画エンジンの置き換え（中核約480行＋全描画命令の書き直し・見た目確認） | 15〜25人日 |
| KeyboardAccelerator/フォーカス管理の再設計（296行超の状態機械をイベントモデルの異なる別体系へ） | 10〜15人日 |
| WinUI3標準コントロール（17ファイル）の置き換え・挙動差異の調整 | 15〜20人日 |
| MainPageコードビハインド全体（約9,700行の大半）のUIイベントハンドラ移植・XAML書き直し | 40〜60人日 |
| 実機動作確認・退行テスト・リリース調整 | 15〜20人日 |
| **合計目安** | **概ね100〜140人日**（1〜2名体制で半年〜8ヶ月程度） |

### 1-4. リスク

- **移行先固有の未知の制約が新たに出るリスク: 高い**。今回のWinUI3既知バグ（Issue #6179）と同様、AvaloniaにもWPFにもそれぞれ固有の未知の制約が一定確率で存在しうる。特にAvaloniaはコミュニティ規模がWinUI3・WPFより小さく、エッジケースの情報が少ない。
- 既存154件のxUnitテストはCore/Pdfのみ対象のため直接の影響は無いが、UI層（作画・テスト・PDF出力の一連の操作フロー）の手動再検証コストが実質ゼロからのUI QAに近い規模で発生する。
- 長期保守コスト: WPFは安定だが新機能追加は停滞、Avaloniaは活発だが破壊的変更のリスクを継続的に負う。

## 2. パターンB: 部分的な技術変更（フォーカス管理まわりのみ）

| 選択肢 | 内容 | 変更範囲・工数 | 評価 |
|---|---|---|---|
| **1. 現状路線の延長**（KeyboardAccelerator+FocusSinkButton方式の改良） | 今回提案した対症療法（`FocusSinkButton.IsHitTestVisible`変更、必要ならデバウンス処理）を発展させる | `MainPage.Pointer.cs`/`MainPage.Properties.cs`中心、数十行規模。1〜3人日 | **推奨**。ただしWinUI3自体のバグが根本にあるため、今後も個別のエッジケースをモグラ叩き的に潰す前提になる |
| **2. Win2D CanvasControlを諦めて別の描画方式に変更** | WriteableBitmap／SwapChainPanel＋DirectX直接制御等 | 描画エンジン置き換えのみでパターンAの一部（15〜25人日）に相当し「部分的」の域を超える | **非推奨**。**今回のバグ原因（FrameLabelBox↔ScrollViewer間のXAML標準コントロールのフォーカス委譲）はWin2D CanvasControl自体と無関係**（CanvasはIsTabStop=Falseでそもそも委譲対象外）。Win2Dを変えても本件の解決には直結しない点に注意 |
| **3. グローバルフック（Win32 SetWindowsHookEx / Raw Input）でOS入力レベルを拾う** | `WH_KEYBOARD_LL`等の低レベルフックでアプリ外のキー処理系を迂回 | 実装自体は5〜10人日だが検証コストが読みにくい | **非推奨**。`WH_KEYBOARD_LL`はシステム全体のフックで自アプリ以外のキー入力も拾ってしまい、P/Invoke必須・セキュリティソフト誤検知・スレッドメッセージループ制約等の副作用が大きい。Microsoft自身も多くの場合Raw Inputを推奨しており、自アプリ内で完結する要件には過剰な手段（[LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)） |

## 3. 総合的な推奨コメント

- パターンAは実測規模が想定の半分以下（約9,700行）と判明し当初印象より現実的だが、依然として**100人日超の大規模プロジェクト**になる。
- 今回のバグ1件のためだけにフレームワーク全面移行を行うのは費用対効果が見合わない。発生頻度・実害が限定的（殿も保留・既知の制限事項化を承認済み）であることを踏まえると、**パターンB選択肢1（現状路線の延長）で個別対応を継続するのが妥当**。
- 将来、WinUI3由来の同種の問題が頻発するようであれば、その時点で改めてパターンAを本格検討する材料が揃う。**現時点では時期尚早**というのが調査結論。
