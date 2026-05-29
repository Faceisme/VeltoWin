# Velto Windows 构建脚本。
#
# 用法:
#   .\build.ps1                       # Release 单文件发布 + 复制一份到桌面(带时间戳)
#   .\build.ps1 -Run                  # 发布后立刻启动
#   .\build.ps1 -NoCopy               # 发布但不复制到桌面
#   .\build.ps1 -Configuration Debug  # Debug 构建到 src\bin\Debug\...(不发布、不复制)
#
# Release 产物:
#   publish\Velto.exe                 — 自包含单文件 (win-x64),换台机器也能直接跑
#   <桌面>\Velto-yyyyMMdd-HHmm\Velto.exe — 存档副本

param(
    [string]$Configuration = "Release",
    [switch]$Run,
    [switch]$NoCopy
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# dotnet 不在 PATH 时兜底(全新装的 SDK 在已开的终端里可能还没刷到 PATH)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $fallback = "C:\Program Files\dotnet"
    if (Test-Path (Join-Path $fallback "dotnet.exe")) {
        $env:Path = "$fallback;$env:Path"
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# 先把还在跑的旧 Velto 干掉,免得 exe 被锁
Get-Process Velto -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "停止已运行的 Velto (PID=$($_.Id))..."
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 200

$projectPath = Join-Path $ScriptDir "src\Velto.csproj"

if ($Configuration -eq "Release") {
    $publishDir = Join-Path $ScriptDir "publish"
    Write-Host "→ dotnet publish (Release, win-x64, 自包含单文件)"
    & dotnet publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败,退出码 $LASTEXITCODE" }

    $exe = Join-Path $publishDir "Velto.exe"
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "→ 产物: $exe  ($sizeMB MB)"

    if (-not $NoCopy) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $stamp = Get-Date -Format "yyyyMMdd-HHmm"
        $destDir = Join-Path $desktop "Velto-$stamp"
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        Copy-Item $exe (Join-Path $destDir "Velto.exe") -Force
        Write-Host "→ 已复制到桌面: $destDir\Velto.exe"
    }
}
else {
    Write-Host "→ dotnet build ($Configuration)"
    & dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败,退出码 $LASTEXITCODE" }
    $exe = Join-Path $ScriptDir "src\bin\$Configuration\net8.0-windows\Velto.exe"
    Write-Host "→ 产物: $exe"
}

if ($Run) {
    Write-Host "→ 启动 Velto..."
    Start-Process -FilePath $exe
}
