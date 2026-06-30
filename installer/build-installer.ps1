# installer/build-installer.ps1
# リポジトリルートから実行: .\installer\build-installer.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- 0. リポジトリルートへ移動 ---
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# --- 1. Inno Setup の確認・インストール ---
function Find-Iscc {
    # PATH から検索
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # 既知のインストール先を検索（システム・ユーザー両対応）
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host "Inno Setup が見つかりません。winget でインストールします..." -ForegroundColor Yellow
    winget install JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    # PATHを更新してから再検索
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
    $iscc = Find-Iscc
    if (-not $iscc) {
        Write-Error "Inno Setup のインストールに失敗しました。手動でインストールしてください。"
        exit 1
    }
}
Write-Host "Inno Setup: $iscc" -ForegroundColor Green

# --- 2. dotnet publish ---
Write-Host "`ndotnet publish を実行中..." -ForegroundColor Cyan
dotnet publish src\GuiEcad.App\GuiEcad.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:WindowsPackageType=None
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish に失敗しました。"; exit 1 }
Write-Host "publish 完了" -ForegroundColor Green

# --- 3. Inno Setup コンパイル ---
Write-Host "`nインストーラーをビルド中..." -ForegroundColor Cyan
& $iscc "installer\GuiEcad_Setup.iss"
if ($LASTEXITCODE -ne 0) { Write-Error "インストーラーのビルドに失敗しました。"; exit 1 }

# --- 4. 完成ファイルを表示 ---
$output = Get-ChildItem "installer\GuiEcad_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($output) {
    Write-Host "`nインストーラーが完成しました:" -ForegroundColor Green
    Write-Host $output.FullName -ForegroundColor White
} else {
    Write-Host "`nインストーラーファイルが見つかりません。ISCC の出力を確認してください。" -ForegroundColor Yellow
}
