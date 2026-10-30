# Windows: a per-user Run key entry that runs `cscode --background` at login (opt-in). Untested on this Linux machine; docs/SESSIONS.md.
#   powershell -File tools/autostart/install-windows.ps1 [-CsCode C:\path\cscode.exe] [-Uninstall]
param([string]$CsCode = (Get-Command cscode -ErrorAction SilentlyContinue).Source, [switch]$Uninstall)
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Uninstall) {
    Remove-ItemProperty -Path $key -Name 'CsCodeBackground' -ErrorAction SilentlyContinue
    return
}
if (-not $CsCode -or -not (Test-Path $CsCode)) { throw 'cscode.exe not found; pass -CsCode' }
Set-ItemProperty -Path $key -Name 'CsCodeBackground' -Value "`"$CsCode`" --background"
Write-Output 'CsCode starts in the background at login.'
