# Screenshot helper for panel verification.
#
# IMPORTANT: must declare DPI awareness BEFORE touching Screen/GDI.
# Without it this process is DPI-unaware: Screen.Bounds reports *logical* size
# (e.g. 1536x960) while CopyFromScreen actually grabs *physical* pixels, so on a
# 200% scaled display it only captures the top-left quarter of the screen.
# That silently crops the right half of any window and looks like a layout bug.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/screenshot.ps1 [-WaitMs 3000] [-Out <path>]

param(
    [int]$WaitMs = 0,
    [string]$Out = ""
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiAwareness
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][DpiAwareness]::SetProcessDPIAware()

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if ($WaitMs -gt 0) { Start-Sleep -Milliseconds $WaitMs }

if ([string]::IsNullOrWhiteSpace($Out)) {
    $dir = Join-Path $env:LOCALAPPDATA "DshPanel2\shots"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $Out = Join-Path $dir ("shot-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".png")
}

# Full virtual screen (covers multi-monitor and negative origins)
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen

$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$gfx.Dispose()

$parent = Split-Path -Parent $Out
if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$w = $bmp.Width
$h = $bmp.Height
$bmp.Dispose()

Write-Output ("saved {0} ({1}x{2})" -f $Out, $w, $h)
