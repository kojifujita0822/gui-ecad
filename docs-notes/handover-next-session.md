# 引き継ぎメモ（次回セッション向け）

> 最終更新: 2026-07-01（家老）。本セッションは家老・侍・忍者・隠密の4役で稼働、v1.0.61リリースをもって解散。

## 本セッションの成果

1. **キーボード配置モードのフォーカス不具合を一式修正**（ラウンド4〜8、docs-notes/keyboard-input-fix-plan.md参照）
   - 機器名/コメント/行コメント編集確定後、方向キーが下部パネルTabViewやMenuBarに奪われる不具合 → `FocusSinkButton`（非表示Button）へのフォーカス退避方式で解決・実機確認済み。
   - 枠ラベルのみ、真因がWinUI3自体の既知バグ（[Issue #6179](https://github.com/microsoft/microsoft-ui-xaml/issues/6179)、Microsoft側修正しない方針）と判明。低リスクな回避策も効果なく、殿判断により**保留・既知の制限事項として記録**（[docs/known-issues.md](../docs/known-issues.md)）。
2. **マウスレス配置（キーボード配置モード）機能を非表示化**（コミット99a8eca）。動作不安定と判断し、ツールバーの`KeyboardModeBtn`を`Visibility=Collapsed`に。**コード自体（HandleKeyboardModeKey等）は削除せず残置**。
3. **UIフレームワーク技術スペック変更の見積もり調査**（[docs-notes/ui-framework-migration-estimate.md](ui-framework-migration-estimate.md)）: WinUI3→WPF/Avalonia全面移行は100〜140人日、費用対効果に見合わず見送り。
4. **v1.0.61向けマウスレス配置除去の見積もり調査**（[docs-notes/v1061-mouseless-removal-estimate.md](v1061-mouseless-removal-estimate.md)）: 結果的にコード分離ではなくUI非表示化のみを採用（上記2）。
5. **v1.0.61 リリース完了**: タグ・GitHub Release・インストーラー添付まで完了。https://github.com/kojifujita0822/gui-ecad/releases/tag/v1.0.61
   - リリース時、バージョン番号更新コミット漏れでタグの付け直しが発生した（教訓: バージョン番号更新→ビルド確認まで終えてからタグを打つこと）。

## 次回セッションへの申し送り

- **未対応の軽微な表示上の癖**（忍者発見、2026-07-01）: Undo実行直後、削除された要素の選択状態（プロパティパネル・Canvas上のハイライト）が即座にクリアされず、別要素をクリックするまで「まだ選択されている」ように見える。データ自体は正しく削除されている（実害なし）。`_selected`参照のクリア漏れの可能性（未調査・推測）。対応要否は次回殿の判断待ち。
- **マウスレス配置機能の今後**: 現状は非表示化のみ（コードは残置）。今後、動作を安定させて再度有効化するか、正式に機能ごと削除するかは未決定。方針が決まれば次回着手。
- **枠ラベル早期確定バグ**: [docs/known-issues.md](../docs/known-issues.md)参照。WinUI3側のバグ由来で保留中。同種のフォーカス委譲不具合が他にも見つかれば、この既知パターン（`ScrollViewer`等タブオーダー内要素への暗黙委譲）を疑うとよい。
- **docs/todo.mdは特に新規タスクなし**。次に何を着手するかは次回セッション冒頭で殿に確認すること。
