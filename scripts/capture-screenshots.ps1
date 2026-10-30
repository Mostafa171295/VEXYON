<#
  Captures real screenshots of every VEXYON tab (runs on a Windows machine / GitHub Actions runner).
  Usage:  powershell -File scripts/capture-screenshots.ps1 -Exe path\to\VEXYON.exe -OutDir docs\screenshots
#>
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$OutDir = "docs/screenshots"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Window([IntPtr]$hwnd, [string]$path) {
  $r = New-Object Win32+RECT
  [void][Win32]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][Win32]::PrintWindow($hwnd, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "saved $path ($w x $h)"
}

$proc = Start-Process -FilePath $Exe -PassThru
try {
  # wait for the main window (first launch of a single-file exe extracts native libs)
  $deadline = (Get-Date).AddSeconds(90)
  while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500; $proc.Refresh()
  }
  if ($proc.MainWindowHandle -eq 0) { throw "VEXYON window did not appear" }
  $hwnd = $proc.MainWindowHandle
  [void][Win32]::SetForegroundWindow($hwnd)
  Start-Sleep -Seconds 6   # let startup checks and the live metrics bar settle

  $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
  $tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
  $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)

  # Arabic tab header -> file name
  $map = [ordered]@{
    "ابدأ هنا"   = "01-start-here"
    "المحادثة"   = "02-chat"
    "المكتبة"    = "03-library"
    "الأدوات"    = "04-tools"
    "الإعدادات"  = "05-settings"
    "السجل"      = "06-log"
    "عن VEXYON"  = "07-about"
  }

  Write-Host ("Found {0} tab(s): {1}" -f $tabs.Count, (($tabs | ForEach-Object { $_.Current.Name }) -join " | "))
  $saved = 0
  foreach ($tab in $tabs) {
    $name = $tab.Current.Name
    if (-not $map.Contains($name)) { Write-Host "skip tab '$name' (not in map)"; continue }
    try {
      $sel = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
      $sel.Select()
      Start-Sleep -Milliseconds 1500
      Save-Window $hwnd (Join-Path $OutDir ($map[$name] + ".png"))
      $saved++
    } catch { Write-Warning "tab '$name' failed: $_" }
  }
  if ($saved -eq 0) { throw "No tabs were captured - check the tab names printed above" }
}
finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
