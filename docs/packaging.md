# パッケージング・配布手順（unpackaged self-contained）

> **方針**: MSIX を使わず、**unpackaged（パッケージ ID なし）＋ self-contained（.NET ランタイム・Windows App SDK ランタイムを同梱）** のフォルダ配布とする。
> 受け取り側は **.NET 8 ランタイムも Windows App SDK ランタイムもインストール不要**で、フォルダごとコピーして `GuiEcad.App.exe` を起動するだけで動く。
> 立案・整備: 2026-06-22。出力先の RID ツリー注意は [setup.md](setup.md) と CLAUDE.md「ビルド出力ツリーの注意」を参照。

---

## 0. なぜこの方式か

| 候補 | 採否 | 理由 |
|---|---|---|
| **unpackaged self-contained**（採用） | ○ | インストーラ・ストア登録・証明書が不要。社内/手渡し配布に最適。ランタイム同梱で「動かない」事故が出にくい。 |
| MSIX パッケージ | 保留 | 署名証明書と配布チャネル整備が必要。将来ストア配布時に検討。 |
| framework-dependent（ランタイム別途インストール） | 不採用 | WinUI 3 はランタイム前提が分かりにくく、配布先での導入トラブルの温床になる。 |

---

## 1. 前提となる csproj 設定（根拠）

`src/GuiEcad.App/GuiEcad.App.csproj` に配布方針を支える設定が入っている。**変更しないこと。**

| プロパティ | 値 | 意味 |
|---|---|---|
| `WindowsAppSDKSelfContained` | `true` | Windows App SDK ランタイムを出力へ同梱（受け取り側に Runtime 導入不要）。 |
| `WinUISDKReferences` | `false` | WinUI SDK 参照を自前解決（self-contained 構成に必要）。 |
| `PublishTrimmed` | `False` | **WinUI 3 はアセンブリトリミング非対応**。有効だと `dotnet build -c Release` が `NETSDK1102` で失敗する。常に無効。 |
| `PublishReadyToRun` | Release で `True` | 起動を高速化（R2R プリコンパイル）。Debug は `False`。 |
| `Platforms` | `x86;x64;ARM64` | AnyCPU 不可。配布は **win-x64** を基本とする。 |
| `Version` | `1.0.0` | 配布物のバージョン。About 画面はここから動的取得。番号変更はこの 1 箇所のみ。 |

---

## 2. 配布ビルド手順

### 2.1 事前チェック（必須）
1. テスト全合格を確認:
   ```pwsh
   dotnet test GuiEcad.sln
   ```
   - 特に [test-plan.md](test-plan.md) 1.2「パッケージング前の穴埋め」に対応する `PrePackagingTests`
     （永続化往復・旧ファイル後方互換・描画スモーク・大規模図面の性能番兵）が緑であること。
2. [test-plan.md](test-plan.md) の **実機チェックリスト**（配置・選択・ドラッグ・コピペ・検索・PDF 出力・テストモード）を一通り確認。
3. `Version` を必要に応じて更新（`GuiEcad.App.csproj` の `<Version>`）。

### 2.2 publish（unpackaged self-contained）
```pwsh
# win-x64 向け配布フォルダを生成（unpackaged = WindowsPackageType=None）
dotnet publish src/GuiEcad.App/GuiEcad.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None
```
- `WindowsPackageType=None` … パッケージ ID を持たない通常の Win32 EXE として発行（MSIX 化しない）。
- `--self-contained true` … .NET 8 ランタイムを同梱。
- `WindowsAppSDKSelfContained=true` は csproj 既定のため明示不要。
- ARM 機向けが必要なら `-r win-arm64` に差し替えてもう一式作る（x86 は通常不要）。

### 2.3 出力物
```
src/GuiEcad.App/bin/Release/net8.0-windows10.0.26100.0/win-x64/publish/
```
- 上記 `publish/` フォルダ一式が配布物。`GuiEcad.App.exe` が起動エントリ。
- self-contained のため依存 DLL・ランタイムが同梱され、サイズは大きい（数百 MB 規模）。これは仕様。

---

## 3. 配布・受け渡し

1. `publish/` フォルダを zip 化して配布（フォルダ構造を保つこと。EXE 単体取り出しは不可）。
2. 受け取り側は任意の場所へ展開し `GuiEcad.App.exe` をダブルクリック。インストール作業は不要。
3. 図面ファイル（`.GCAD`）は配布物と独立。ユーザーの任意フォルダで保存・読込する。
4. 設定（パレット位置 `palette-pos.txt`・テーマ `ui-theme.txt` 等）は実行時に
   `マイドキュメント\GuiEcad\` 配下へ作られる。配布物には含めない。

---

## 4. 既知の注意点

- **RID ツリーの取り違え**: 開発時の `dotnet run` / `-r win-x64` と、`-p:Platform=x64` は出力先が別ツリー
  （`bin\x64\...`）。配布 publish では `-r win-x64` を使い、`-p:Platform=x64` は使わない。詳細は CLAUDE.md
  「ビルド出力ツリーの注意」。
- **トリミング不可**: `PublishTrimmed=True` にしてサイズ削減を狙うと WinUI 3 では `NETSDK1102` で失敗する。やらない。
- **SmartScreen**: 署名なし EXE は初回起動時に Windows SmartScreen 警告が出ることがある（「詳細情報」→「実行」で起動可）。
  署名が必要になった段階で MSIX or Authenticode 署名を別途検討する。
- **アイコン/メタ情報**: 製品名 `GuiEcad`・説明・発行者（Company/Authors=`FK TEQUNO`）・著作権（`Copyright © 2026 FK TEQUNO`）を csproj に設定済みでファイルプロパティへ反映される。MSIX 用 `Package.appxmanifest` も PublisherDisplayName=`FK TEQUNO`・Publisher=`CN=FK TEQUNO`・DisplayName=`GuiEcad`・Version=`1.0.0.0` に整備済み（Identity Name は既定 GUID のまま。実署名時に証明書の Subject と一致させる）。

---

## 5. 将来検討（保留）

- MSIX パッケージ化（ストア/署名配布が必要になった場合）。`EnableMsixTooling=true` は既に有効で土台はある。
- 自動更新の仕組み（現状は手動でフォルダ差し替え）。
- インストーラ（MSI/Inno Setup 等）でのスタートメニュー登録（現状はフォルダ起動）。
