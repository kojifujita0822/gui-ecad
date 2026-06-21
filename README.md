# gui_ecad

GUI で入力できるラダー図作成用の **Windows デスクトップアプリケーション**（C# / .NET 8 / WinUI 3）。
作図の記法はラダー図的だが、記入・運用の流儀は「シーケンス図（展開接続図）」スタイル（実機器名・設置場所枠・クロスリファレンス）に準拠する。**作画モード**で図面作成、**テストモード**で動作確認を行う。

## 主な技術
- UI: WinUI 3（Windows App SDK）／画面描画: Win2D
- ベクター PDF 出力: PDFsharp
- 描画は `IRenderer` 抽象で画面と PDF を同一コード化
- 永続化: JSON 単一ファイル（拡張子 `.GCAD`）
- テストモード: 有接点リレーの connectivity ＋ 不動点評価（評価順序非依存・発振検出）

## プロジェクト構成
| プロジェクト | 役割 |
|---|---|
| `src/GuiEcad.Core` | データモデル・シミュレーション・`IRenderer` 抽象（UI非依存） |
| `src/GuiEcad.Pdf` | PDFsharp による PDF 出力 |
| `src/GuiEcad.App` | WinUI 3 本体（Win2D 描画） |
| `tests/GuiEcad.Tests` | xUnit テスト |

## ビルド / 実行 / テスト
```pwsh
# WinUI3 アプリは RID 指定でビルド/実行する（win-x64 ツリーに統一・AnyCPU 不可）
# ※ -p:Platform=x64 は出力先が別ツリー(bin\x64)になり旧バイナリ実行の事故元なので使わない
dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64
dotnet run   --project src/GuiEcad.App

# ライブラリ / テスト
dotnet build GuiEcad.sln
dotnet test  GuiEcad.sln
```
前提ツールの導入は [docs/setup.md](docs/setup.md) を参照。

## ドキュメント
設計の詳細は [CLAUDE.md](CLAUDE.md)（索引）と `docs/` を参照。次の実装計画は [docs/next-plan.md](docs/next-plan.md)。
