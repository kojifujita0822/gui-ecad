; GuiEcad インストーラー（Inno Setup 6.x）
; ビルド前に dotnet publish を実行しておくこと（docs/packaging.md 参照）

#define AppName      "GuiEcad"
#define AppVersion   "1.0.7"
#define AppPublisher "FK TEQUNO"
#define AppExeName   "GuiEcad.App.exe"
; publish/ フォルダへの相対パス（インストーラーの 1 つ上がリポジトリルート）
#define SourceDir    "..\src\GuiEcad.App\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"

[Setup]
AppId={{E3A7F1C2-4B8D-4E9F-A021-5C6D7E8F9A0B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/kojifujita0822
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=GuiEcad_Setup_{#AppVersion}
OutputDir=.
Compression=lzma2
SolidCompression=yes
; 64 ビット専用インストール
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; アンインストーラーをコントロールパネルに登録
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; 管理者権限（ProgramFiles への書き込みに必要）
PrivilegesRequired=admin
; ファイル関連付けの変更をエクスプローラーに通知
ChangesAssociations=yes
; 上書きインストール時に起動中アプリを自動終了
CloseApplications=yes
; exeプロパティのファイルバージョン（Major.Minor.Patch.0 形式）
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する(&D)"; GroupDescription: "追加タスク:"; Flags: checkedonce

[InstallDelete]
; 上書きインストール前に旧バージョンのファイルをすべて削除（残留DLL等の競合防止）
Type: filesandordirs; Name: "{app}"

[Files]
; publish/ フォルダの全ファイルを再帰的にインストール
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; スタートメニュー
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} のアンインストール"; Filename: "{uninstallexe}"
; デスクトップ（オプション）
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; .gcad ファイル関連付け
Root: HKCR; Subkey: ".gcad"; ValueType: string; ValueName: ""; ValueData: "GuiEcad.Document"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "GuiEcad.Document"; ValueType: string; ValueName: ""; ValueData: "GuiEcad 回路図ファイル"; Flags: uninsdeletekey
Root: HKCR; Subkey: "GuiEcad.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCR; Subkey: "GuiEcad.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
; インストール完了後にアプリを起動（オプション）
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent
