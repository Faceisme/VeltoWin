# Velto Windows 构建脚本。
# 默认输出: publish\Velto.exe (单文件、self-contained、win-x64)
#
# 用法:
#   .\build.ps1             # Release 单文件发布
#   .\build.ps1 -Run        # 构建后立刻启动
#   .\build.ps1 -Configuration Debug   # debug 输出到 src\bin\Debug\net8.0-windows\

param(
    [string]$Configuration = "Release",
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# 先把还在跑的旧 Velto 干掉,免得 exe 被锁
Get-Process Velto -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "停止已运行的 Velto (PID=$($_.Id))..."
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 200

$projectPath = Join-Path $ScriptDir "src\Velto.csproj"

if ($Configuration -eq "Release") {
    $publishDir = Join-Path $ScriptDir "publish"
    Write-Host "→ dotnet publish ($Configuration, win-x64, single-file)"
    & dotnet publish $projectPath `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败,退出码 $LASTEXITCODE" }
    $exe = Join-Path $publishDir "Velto.exe"
    Write-Host "→ 输出: $exe"
}
else {
    Write-Host "→ dotnet build ($Configuration)"
    & dotnet build $projectPath -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败,退出码 $LASTEXITCODE" }
    $exe = Join-Path $ScriptDir "src\bin\$Configuration\net8.0-windows\Velto.exe"
}

if ($Run) {
    Write-Host "→ 启动 Velto..."
    Start-Process -FilePath $exe
}
