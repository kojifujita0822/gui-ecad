# Changelog

このファイルはプロジェクトの変更履歴を記録します。
形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、
バージョン管理は [Semantic Versioning](https://semver.org/lang/ja/) に従います。

---

## [Unreleased]

---

## [1.0.2] - 2026-06-23

### 追加
- 端子台の線番表示を変更：前後の配線ではなく端子台記号の真上に1個だけ表示するよう変更

### 修正
- BOM（部品リスト）ダイアログの数量列がスクロールバーに隠れる表示崩れを修正
- 行コメントの文字サイズを 2.0mm から 3.0mm に拡大
- シート設定ダイアログにシート名入力欄を追加
- NavTree のダブルクリックでシート名を変更できるよう追加

---

## [1.0.1] - 2026-06-23

### 修正
- PDF出力のクロスリファレンス専用ページを A4縦固定に変更（行数に関わらず縦向きで出力されるよう修正）

---

## [1.0.0] - 2026-06-22

### 追加
- アプリアイコンを電気CAD風デザインに刷新
- リリースメタ情報を整備（発行者: FK TEQUNO）
- unpackaged self-contained 形式での配布ビルド対応
- ツールパレットのドック/フロート切替（Jw_cad風・上下端吸着）
- ナビゲーションツリー（シート管理）
- GX Works3 風 UI レイアウト（ツールパレット・右パネル・ステータスバー）
- クロスリファレンス専用ページ・BOM（部品表）ページの PDF 出力
- A4縦図面枠・表題欄・改定欄
- 複数ページ分割（28行/ページ）
- 右クリックメニュー・コピー/ペースト
- ライト/ダークテーマ切替
- プロパティパネル（SelectSwitch ノッチ・Timer 設定時間）
- 接続検査（DRC）・クロスリファレンス DRC
- テストモード（有接点リレーシミュレーション）
- JSON 形式での永続化（.GCAD）
- ベクター PDF 出力（PDFsharp）

---

<!-- リンク定義 -->
[Unreleased]: https://github.com/kojifujita0822/gui-ecad/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/kojifujita0822/gui-ecad/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/kojifujita0822/gui-ecad/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/kojifujita0822/gui-ecad/releases/tag/v1.0.0
