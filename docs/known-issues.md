# 既知の不具合（未修正・保留）

> 修正を試みたが解決に至らず、影響が限定的なため保留と判断したものを記録する。
> 新たに保留判断が下った不具合は本ファイルに追記する。

## 枠ラベル編集の早期確定バグ（2026-07-01・保留・殿承認済み）

**症状**: 枠ラベル（`FrameLabelBox`）をダブルクリックで編集開始した直後に別操作（クリック等）を行うと、文字を入力する前に空ラベルのまま確定されてしまう場合がある。

**原因**: WinUI3 側の既知バグ（[microsoft-ui-xaml Issue #6179](https://github.com/microsoft/microsoft-ui-xaml/issues/6179)、Microsoft 側 not planned＝修正されない方針）。要素が `PointerPressed` を処理していても、対応する `PointerReleased` 発生後にフォーカスがタブオーダー内の別要素（本アプリでは右パネル・プロパティタブの `ScrollViewer`）へ暗黙的に移ってしまう。アプリコードの `Focus()` 呼び出しを一切経由しないフレームワーク側の挙動のため、アプリ側のコードを追っても原因箇所が見つからない。

**試した対応と結果**:
- Commit 系メソッド（`CommitFrameLabel` 等）でのフォーカス明示復帰 → 効果なし（そもそもフォーカスが奪われるのは Commit 呼び出しより前）
- `OnPointerPressed` のガード漏れ修正（`_editingFrame is null` 追加） → 効果なし（2 回目クリックは元々ガード対象の分岐を通らないことが判明）
- `RefreshPropertiesPanel` 冒頭でのフォーカス退避 → 効果なし（1 回目クリックのみで実行され、時系列上は無関係と判明）
- 退避先 `FocusSinkButton` の `IsHitTestVisible=True` 化 → 効果なし（同じ `ScrollViewer` へのフォーカス移動を確認）

**影響範囲**: 機器名・コメント・行コメントの編集確定では同種の問題は解消済み（`FocusSinkButton` へのフォーカス退避で対応、実機確認済み）。枠ラベルのみ、上記 WinUI3 側バグにより未解決。

**実用上の影響**: 限定的（ダブルクリックで編集を開始した直後に別操作をしなければ発生しない）。空ラベルのまま確定された場合も、再度ダブルクリックすれば通常通り編集・入力可能。

**詳細調査の経緯**: [docs-notes/keyboard-input-fix-plan.md](../docs-notes/keyboard-input-fix-plan.md) ラウンド4〜8を参照。
