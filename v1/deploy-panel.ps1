<#
.SYNOPSIS
    把编译好的 DshPanel.exe 部署到目标路径（默认 %LOCALAPPDATA%\DSH）。

.DESCRIPTION
    面板自己常在运行中，于是「覆盖 exe」必然撞上「being used by another process」。
    这个脚本把整条链做成可重试、可校验、可回滚的：

        停进程 → 等句柄释放 → 目标改名 .old → 复制 → 比哈希 → 删 .old

    和手工版（Stop-Process → Move-Item → Copy-Item → 校验 → 删）的区别，全部是踩过的坑：
      * 每步都带 -ErrorAction Stop：Copy-Item 的失败是「非终止错误」，裸 try/catch 抓不住，
        会出现「脚本安静地成功、exe 其实没换」。
      * 停进程后轮询「文件能否独占打开」，而不是盲睡：句柄释放有延迟，睡太短必失败、睡太长纯浪费。
      * 复制后比 SHA256：跨盘/杀软扫描都可能截断，长度对不上也算失败。
      * 任何一步失败都回滚到 .old，绝不留「新 exe 半残、旧 exe 已改名」的中间态。

    两个路径都在沙箱外，所以本脚本要在 **DSH 沙箱之外** 跑（双击 deploy-panel.cmd，
    或在普通 PowerShell 窗口里跑）。在沙箱内跑会被拒，必须走 danger-full-access 提权。

.PARAMETER Source
    要部署的 exe，默认 <脚本目录>\dist\DshPanel.exe。

.PARAMETER Target
    目标路径，默认 $env:LOCALAPPDATA\DSH\DshPanel.exe。

.PARAMETER ProcessName
    需要先停掉的进程名（不带 .exe），默认 DshPanel。

.PARAMETER WaitSeconds
    等进程退出 / 等文件句柄释放的上限秒数，默认 15。

.PARAMETER KeepBackup
    保留 .old 备份（调试时用）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy-panel.ps1

.EXAMPLE
    # 干跑：只检查锁与哈希，不落盘
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy-panel.ps1 -WhatIfOnly
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Target,
    [string]$ProcessName = 'DshPanel',
    [int]$WaitSeconds = 15,
    [switch]$KeepBackup,
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot 在 param() 默认值里可能是空的，所以在主体里解析（两种启动方式都稳）。
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $Source) { $Source = Join-Path $scriptRoot 'dist\DshPanel.exe' }
if (-not $Target) { $Target = Join-Path $env:LOCALAPPDATA 'DSH\DshPanel.exe' }

function Write-Step([string]$Text) { Write-Host "[deploy] $Text" }
function Write-Fail([string]$Text) { Write-Host "[deploy] [X] $Text" -ForegroundColor Red }

# 能不能独占打开：能打开就说明没有进程还在用这个 exe。
function Test-FileUnlocked([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $stream.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Wait-FileUnlocked([string]$Path, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-FileUnlocked -Path $Path) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return (Test-FileUnlocked -Path $Path)
}

function Get-Sha([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# ---- 1. 源文件 ----
if (-not (Test-Path -LiteralPath $Source)) {
    Write-Fail "找不到要部署的 exe：$Source"
    Write-Host '        先跑 build-panel.cmd（或 csc 直接编译）生成 dist\DshPanel.exe。'
    exit 1
}
$sourceHash = Get-Sha -Path $Source
$sourceItem = Get-Item -LiteralPath $Source
Write-Step "源：$Source"
Write-Step "     $($sourceItem.Length) 字节  SHA256 $($sourceHash.Substring(0, 16))…"

# ---- 2. 停进程 ----
$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step "停止 $ProcessName（PID $($running.Id -join ', ')）"
    if ($WhatIfOnly) {
        Write-Step '     （-WhatIfOnly：不真的停）'
    } else {
        $running | Stop-Process -Force
        try { Wait-Process -Id $running.Id -Timeout $WaitSeconds -ErrorAction Stop }
        catch { Write-Step '     （等进程退出超时，继续尝试等文件句柄）' }
    }
} else {
    Write-Step "没有正在运行的 $ProcessName"
}

# ---- 3. 等目标文件句柄释放 ----
if (-not (Wait-FileUnlocked -Path $Target -Seconds $WaitSeconds)) {
    Write-Fail "目标文件仍被占用，放弃部署：$Target"
    Write-Host "        常见原因：面板被托盘图标重新拉起、或另一个用户会话还开着它。"
    Write-Host "        处理：右键托盘退出面板，再重跑本脚本。"
    exit 2
}

$targetHash = Get-Sha -Path $Target
if ($targetHash -eq $sourceHash) {
    Write-Step '目标已是最新版本（SHA256 相同），无需部署。'
    exit 0
}

if ($WhatIfOnly) {
    Write-Step '（-WhatIfOnly：到此为止，未写入任何文件）'
    exit 0
}

# ---- 4. 目标目录 ----
$targetDir = Split-Path -Parent $Target
if (-not (Test-Path -LiteralPath $targetDir)) {
    Write-Step "创建目录 $targetDir"
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

# ---- 5. 旧文件改名 ----
$backup = "$Target.old"
$hadBackup = $false
if (Test-Path -LiteralPath $Target) {
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force -ErrorAction Stop }
    Write-Step '旧 exe 改名为 .old'
    Move-Item -LiteralPath $Target -Destination $backup -Force -ErrorAction Stop
    $hadBackup = $true
}

# ---- 6. 复制 + 校验（失败回滚） ----
try {
    Write-Step '复制新 exe'
    Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop

    if (-not (Wait-FileUnlocked -Path $Target -Seconds 5)) {
        throw '复制完成但文件仍被占用（可能被杀软扫描中）'
    }
    $newHash = Get-Sha -Path $Target
    if ($newHash -ne $sourceHash) {
        throw "哈希不一致（期望 $sourceHash，实际 $newHash）"
    }
    Write-Step '校验通过'
} catch {
    Write-Fail "部署失败：$($_.Exception.Message)"
    Write-Step '回滚'
    try {
        if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Force -ErrorAction Stop }
        if ($hadBackup) { Move-Item -LiteralPath $backup -Destination $Target -Force -ErrorAction Stop }
        Write-Step '已回滚到旧版本'
    } catch {
        Write-Fail "回滚也失败了，请手工处理：$Target / $backup"
    }
    exit 3
}

# ---- 7. 收尾 ----
if ($hadBackup -and -not $KeepBackup) {
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Write-Step '已删除 .old 备份'
} elseif ($hadBackup) {
    Write-Step "保留备份：$backup"
}

Write-Step "完成：$Target"
Write-Host '[deploy] 提示：面板若设了开机自启/托盘常驻，记得重新启动它才能用上新版本。' -ForegroundColor Green
exit 0
