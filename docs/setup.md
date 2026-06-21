# セットアップ / プロジェクト構成

> **状態**: 雛形生成済み（.NET 8.0.422）。ソリューション `GuiEcad.sln`／4プロジェクトをビルド・テスト確認済み。

## 前提ツールの導入
```pwsh
# .NET 8 SDK（LTS・安定版）
winget install Microsoft.DotNet.SDK.8

# （推奨）WinUI 3 開発ワークロード込みの Visual Studio 2022
#   「.NET デスクトップ開発」＋「Windows App SDK / WinUI」を選択
winget install Microsoft.VisualStudio.2022.Community
```
インストール後、新しいターミナルで `dotnet --version` が通ることを確認する。

## 想定プロジェクト構成
```
gui_ecad/
├─ GuiEcad.sln
├─ src/
│  ├─ GuiEcad.Core/   # UI非依存: Model / Simulation / Rendering(IRenderer抽象)。依存なし
│  ├─ GuiEcad.Pdf/    # PDFsharp による IRenderer 実装（PDF出力専用）。依存: Core + PDFsharp
│  └─ GuiEcad.App/    # WinUI 3 本体: Views/ViewModels + Win2D の IRenderer 実装。依存: Core(+Pdf)
└─ tests/
   └─ GuiEcad.Tests/  # xUnit。依存: Core(+Pdf)
```

- **テストフレームワーク: xUnit**（確定）。
- **NuGet 依存（導入済み）**:
  - `GuiEcad.App`: `Microsoft.WindowsAppSDK`（テンプレート同梱）, `Microsoft.Graphics.Win2D`
  - `GuiEcad.Pdf`: `PDFsharp` 6.2.4
  - `GuiEcad.Tests`: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`
- ターゲット: Core/Pdf/Tests は `net8.0`、App は `net8.0-windows10.0.26100.0`（WinUI 3、`win-x64`）。
- **Core 既存コード**: `Rendering/IRenderer.cs`（`IRenderer` / `IRenderSurface` と関連型）。

## 雛形生成の記録（実行済みコマンド）
WinUI3 テンプレートは公式パッケージ **`Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`**（短縮名 `winui` = Blank App）を使用。
```pwsh
dotnet new sln -n GuiEcad
dotnet new classlib -n GuiEcad.Core  -o src/GuiEcad.Core
dotnet new classlib -n GuiEcad.Pdf   -o src/GuiEcad.Pdf
dotnet new xunit    -n GuiEcad.Tests -o tests/GuiEcad.Tests
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
dotnet new winui    -n GuiEcad.App   -o src/GuiEcad.App
dotnet sln add src/GuiEcad.Core src/GuiEcad.Pdf src/GuiEcad.App tests/GuiEcad.Tests
# 参照: Pdf→Core / Tests→Core,Pdf / App→Core,Pdf
# NuGet: Pdf に PDFsharp、App に Microsoft.Graphics.Win2D
```

## ビルド / 実行 / テスト
```pwsh
# WinUI3 アプリは RID 指定でビルド/実行する（win-x64 ツリーに統一）
# ※ -p:Platform=x64 は別ツリー(bin\x64)へ出力し旧バイナリ実行の事故元になるため使わない（2026-06-21 案A）
dotnet build src/GuiEcad.App/GuiEcad.App.csproj -r win-x64
dotnet run   --project src/GuiEcad.App
# Core/Pdf/Tests（クラスライブラリ/テスト）
dotnet build GuiEcad.sln
dotnet test  GuiEcad.sln
dotnet test  GuiEcad.sln --filter "FullyQualifiedName~<テスト名>"   # 単体
```

> 注: WinUI 3 アプリは AnyCPU 不可（x64/x86/arm64 が必要）。`dotnet run`/`-r win-x64` は既定で win-x64 RID ツリー（`bin\Debug\...\win-x64`）を使う。`-p:Platform=x64` は出力先が `bin\x64\Debug` と別ツリーになり古いバイナリを実行する事故の元なので使わない。配布時は MSIX パッケージ化を別途検討。
