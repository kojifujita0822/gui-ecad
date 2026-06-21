# 自作パーツ作成ツール — 実装記録

> **完了: 2026-06-19**。ステップ A〜C 実装済み。残はステップ D（外部ファイル運用）のみ。

## 完了したもの

### ステップ A: パーツエディタ画面
- [x] `PartEditorWindow`（別ウィンドウ、Win2D キャンバス）
- [x] 図形プリミティブ描画ツール: 線 / 円 / 矩形 / 文字
- [x] 図形の選択・移動・削除・Undo

### ステップ B: 接続点と役割
- [x] 接続点（`PortDef`）の配置（基準範囲の境界グリッドにスナップ）
- [x] 役割（`PartRole`: 接点NO/NC・コイル・ランプ・端子・非シミュレート）の指定

### ステップ C: 保存と呼び出し
- [x] `PartLibrary` への保存（JSON 永続化・`.GCAD` 埋め込み）
- [x] 「その他部品」メニューの PartId 対応（自作パーツを動的列挙・配置）
- [x] `ElementInstance.PartId` 参照 → `PartResolver`/`PartDrawing` 経由で描画

## 未着手

### ステップ D: ライブラリ運用
- [x] パーツライブラリの外部ファイルエクスポート/インポート（`PartLibrarySerializer`・`.gcadparts` 単独JSON。「その他部品」メニューから入出力）
- [x] 既存パーツの再編集 UI（「その他部品」メニューの各パーツに 配置/編集/削除 サブメニュー。編集は同 Id 上書き）
- [ ] パレットへの登録・並び替え UI（置き場所・操作方法の設計判断が必要・保留）

詳細は [docs/todo.md](todo.md) を参照。

## 基盤実装（Core 側・テスト済み）

| クラス | 内容 |
|---|---|
| `PartDefinition` | 基準範囲 W×H セル＋プリミティブ＋PortDef＋PartRole |
| `PartLibrary` | パーツ集合・JSON 多態シリアライズ |
| `PartResolver` | 組込み種別/自作パーツの統一解決 |
| `PartDrawing` | プリミティブから IRenderer で描画 |
| テスト | 4件（シミュレーション・接続・JSON往復・PDF描画） |
