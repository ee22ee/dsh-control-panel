# Create (or overwrite) the desktop shortcut for DSH Panel.
# Points at the RELEASE build in dist\ so it is independent of bin\Debug.
# Pure ASCII on purpose: PowerShell 5.1 reads UTF-8 .ps1 as ANSI, Chinese literals break parsing.
# The Chinese shortcut name is applied afterwards by bash `mv`.

$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'dist\DshPanel2.exe'
$ico = Join-Path $projectRoot 'dist\DeepSeekHarness-WhaleGirl.ico'

if (-not (Test-Path $exe)) {
    Write-Output "ERR: release exe not found: $exe"
    exit 1
}

$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop 'DSH Panel.lnk'

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath       = $exe
$lnk.WorkingDirectory = (Join-Path $projectRoot 'dist')
$lnk.IconLocation     = "$ico,0"
$lnk.Description      = 'DSH 控制面板'
$lnk.WindowStyle      = 1
$lnk.Save()

Write-Output "TARGET : $exe"
Write-Output "LNK    : $lnkPath"
Write-Output "ICON   : $($lnk.IconLocation)"
Write-Output "EXISTS : $(Test-Path $exe)"
