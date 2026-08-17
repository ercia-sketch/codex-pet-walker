$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class PetRestoreNative
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
}
'@

$restored = $false
[PetRestoreNative]::EnumWindows({
    param([IntPtr] $window, [IntPtr] $unused)
    $processId = [uint32] 0
    [void] [PetRestoreNative]::GetWindowThreadProcessId($window, [ref] $processId)
    try { $process = Get-Process -Id $processId -ErrorAction Stop } catch { return $true }
    if ($process.ProcessName -ne 'ChatGPT') { return $true }
    $title = [Text.StringBuilder]::new(128)
    $className = [Text.StringBuilder]::new(128)
    [void] [PetRestoreNative]::GetWindowText($window, $title, $title.Capacity)
    [void] [PetRestoreNative]::GetClassName($window, $className, $className.Capacity)
    if ($title.ToString() -eq 'Codex' -and $className.ToString() -eq 'Chrome_WidgetWin_1') {
        [void] [PetRestoreNative]::ShowWindow($window, 8)
        $script:restored = $true
        return $false
    }
    return $true
}, [IntPtr]::Zero) | Out-Null

[pscustomobject] @{ Restored = $restored } | Format-List
