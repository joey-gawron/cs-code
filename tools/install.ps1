<#
.SYNOPSIS
  Installs CsCode for the current Windows user: a Start Menu shortcut and `cscode` on the user's PATH. No administrator rights.

.DESCRIPTION
  UNTESTED: written on Linux (the Windows counterpart of tools/install.sh, docs/briefs/app-icon-and-install.md, SHOULD); only its syntax is
  checked here (tests/CsCode.App.Tests, InstallPs1Tests, with PowerShell 7 when it is available). Run it from a CsCode source tree.

  Layout (per user, %LOCALAPPDATA%\Programs\CsCode):
    <version>\              the app (self-contained win-x64 publish: its own .NET runtime), debug-adapters\ when given, uninstall copy
    current                 a directory junction to the installed version (no administrator rights needed); the previous version is kept
    bin\cscode.cmd          the command-line launcher (passes every argument; DOTNET_ROOT only when unset and a dotnet is found; adds
                            %USERPROFILE%\.dotnet\tools to PATH for the processes the app starts); bin\ is added to the user's PATH
    install-manifest.json   what this script created, for -Uninstall
  Start Menu: %APPDATA%\Microsoft\Windows\Start Menu\Programs\CsCode.lnk (icon: the executable's, assets\branding\cscode.ico).

  Idempotent: a rerun installs the build as a new version, moves `current`, keeps the one before and removes older ones. -Uninstall removes
  exactly what the manifest lists (and the PATH entry it added) and keeps the user's settings and data (%APPDATA%\CsCode, %LOCALAPPDATA%\CsCode).

.EXAMPLE
  pwsh tools\install.ps1                      # publish (dotnet SDK on PATH) and install
  pwsh tools\install.ps1 -From C:\pub\cscode  # install an existing win-x64 publish
  pwsh tools\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $From,
    [string] $Version,
    [string] $NetCoreDbg,
    [string] $JsDebug,
    [string] $CodeLldb,
    [switch] $NoAdapters,
    [switch] $Uninstall
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$Marker = 'cscode-install-marker'
$Root = Join-Path $env:LOCALAPPDATA 'Programs\CsCode'
$Bin = Join-Path $Root 'bin'
$Launcher = Join-Path $Bin 'cscode.cmd'
$ManifestPath = Join-Path $Root 'install-manifest.json'
$Shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\CsCode.lnk'

function Read-Manifest {
    if (Test-Path -LiteralPath $ManifestPath) { return Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json }
    return $null
}

function Test-InUse([string] $Directory) {
    # A process whose executable lies under the version (the app, or an adapter it started).
    $prefix = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\') + '\'
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try { if ($p.Path -and $p.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true } } catch { }
    }
    return $false
}

function Remove-Version([string] $Name) {
    $dir = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $dir)) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.cscode-install\info'))) { Write-Warning "kept $dir (not installed by this script)"; return }
    Remove-Item -LiteralPath $dir -Recurse -Force
}

function Get-UserPath { return [Environment]::GetEnvironmentVariable('Path', 'User') }

function Set-UserPath([string] $Value) { [Environment]::SetEnvironmentVariable('Path', $Value, 'User') }

if ($Uninstall) {
    $m = Read-Manifest
    if ($null -eq $m) { Write-Output "CsCode is not installed for $env:USERNAME (no $ManifestPath): nothing to remove."; return }
    foreach ($v in $m.versions) {
        if (Test-InUse (Join-Path $Root $v)) { throw "CsCode $v is running: quit it first (cscode --quit --all), then run this again" }
    }
    foreach ($v in $m.versions) { Remove-Version $v }
    $current = Join-Path $Root 'current'
    if (Test-Path -LiteralPath $current) { (Get-Item -LiteralPath $current).Delete() } # the junction only, never its target
    foreach ($f in @($Launcher, $Shortcut)) {
        if (Test-Path -LiteralPath $f) {
            if ($f -eq $Shortcut -or (Select-String -LiteralPath $f -Pattern $Marker -SimpleMatch -Quiet)) { Remove-Item -LiteralPath $f -Force }
            else { Write-Warning "kept $f (replaced after the install)" }
        }
    }
    if ($m.addedPath) {
        $entries = (Get-UserPath) -split ';' | Where-Object { $_ -and $_ -ne $Bin }
        Set-UserPath ($entries -join ';')
    }
    Remove-Item -LiteralPath $ManifestPath -Force
    foreach ($d in @($Bin, $Root)) { if ((Test-Path -LiteralPath $d) -and -not (Get-ChildItem -LiteralPath $d -Force)) { Remove-Item -LiteralPath $d -Force } }
    Write-Output 'CsCode uninstalled. Your settings and data stay (%APPDATA%\CsCode, %LOCALAPPDATA%\CsCode).'
    return
}

$repo = Split-Path -Parent $PSScriptRoot
$branding = Join-Path $repo 'assets\branding'
if (-not (Test-Path -LiteralPath (Join-Path $branding 'cscode.ico'))) { throw 'Run it from a CsCode source tree (assets\branding is missing).' }

# Refuse before doing any work: a cscode.cmd this script did not write is never replaced.
if ((Test-Path -LiteralPath $Launcher) -and -not (Select-String -LiteralPath $Launcher -Pattern $Marker -SimpleMatch -Quiet)) {
    throw "$Launcher exists and was not created by this installer; move it away and run this again"
}
if ($Version -and $Version -notmatch '^[0-9][0-9A-Za-z._+-]*$') { throw '-Version must start with a digit and use only letters, digits and . _ + -' }

# ------------------------------------------------------------------ the build
if ($From) {
    $src = (Resolve-Path -LiteralPath $From).Path
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet was not found: install the .NET 10 SDK, or pass -From <publish dir>' }
    $src = Join-Path $repo '.scratch\publish\win-x64'
    if (Test-Path -LiteralPath $src) { Remove-Item -LiteralPath $src -Recurse -Force }
    Write-Output 'Publishing CsCode (win-x64, self-contained)...'
    # The same settings as tools/publish-app.sh (build/Publish.props: self-contained, ReadyToRun composite).
    & dotnet publish (Join-Path $repo 'src\CsCode.App\CsCode.App.csproj') --nologo -v:q -c Release -r win-x64 --self-contained true `
        --disable-build-servers --artifacts-path (Join-Path $repo '.scratch\publish-artifacts\win-x64') -o $src
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
$exe = Join-Path $src 'cscode.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "$src is not a win-x64 CsCode publish (cscode.exe missing)" }
$appVersion = (& $exe --version | Select-Object -First 1)
if ($appVersion -notmatch '^[0-9]') { $appVersion = '0.0.0' }
if (-not $Version) {
    $commit = (& git -C $repo rev-parse --short=10 HEAD 2>$null)
    $Version = if ($LASTEXITCODE -eq 0 -and $commit) { "$appVersion-g$commit" } else { "$appVersion-$(Get-Date -Format yyyyMMddHHmmss)" }
}

# ------------------------------------------------------------------ the version folder
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$stage = Join-Path $Root (".install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Copy-Item -LiteralPath $src -Destination $stage -Recurse
try {
    if (-not $NoAdapters) {
        # Laid out as src\CsCode.Dap\Adapters\BundledAdapters.cs expects (the tools directory's layout under debug-adapters\); nothing is downloaded.
        $adapters = Join-Path $stage 'debug-adapters'
        if ($NetCoreDbg) {
            $dir = if (Test-Path -LiteralPath $NetCoreDbg -PathType Leaf) { Split-Path -Parent $NetCoreDbg } else { $NetCoreDbg }
            if (Test-Path -LiteralPath (Join-Path $dir 'netcoredbg.exe')) { Copy-Item -LiteralPath $dir -Destination (Join-Path $adapters 'netcoredbg') -Recurse }
            else { Write-Warning "netcoredbg: no netcoredbg.exe in $dir; not bundled" }
        }
        if ($JsDebug) {
            $jsVersion = (Select-String -LiteralPath (Join-Path $repo 'src\CsCode.Dap\Adapters\JsDebugInstaller.cs') -Pattern 'public const string Version = "(v[0-9.]+)"').Matches[0].Groups[1].Value
            $dir = if (Test-Path -LiteralPath (Join-Path $JsDebug 'js-debug\src\dapDebugServer.js')) { Join-Path $JsDebug 'js-debug' } else { $JsDebug }
            if (Test-Path -LiteralPath (Join-Path $dir 'src\dapDebugServer.js')) {
                $target = Join-Path $adapters "js-debug\$jsVersion"
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                Copy-Item -LiteralPath $dir -Destination (Join-Path $target 'js-debug') -Recurse
            } else { Write-Warning "js-debug: no src\dapDebugServer.js under $JsDebug; not bundled" }
        }
        if ($CodeLldb) {
            $lldbVersion = (Select-String -LiteralPath (Join-Path $repo 'src\CsCode.Dap\Adapters\CodeLldbInstaller.cs') -Pattern 'public const string Version = "(v[0-9.]+)"').Matches[0].Groups[1].Value
            $inst = if (Test-Path -LiteralPath (Join-Path $CodeLldb 'extension')) { $CodeLldb } else { Split-Path -Parent $CodeLldb }
            if (Test-Path -LiteralPath (Join-Path $inst 'extension\adapter\codelldb.exe')) {
                $target = Join-Path $adapters "codelldb\$lldbVersion"
                New-Item -ItemType Directory -Force -Path $target | Out-Null
                Copy-Item -LiteralPath (Join-Path $inst 'extension') -Destination $target -Recurse
            } else { Write-Warning "CodeLLDB: no extension\adapter\codelldb.exe under $CodeLldb; not bundled" }
        }
    }
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stage 'install.ps1') # -Uninstall works without the source tree
    New-Item -ItemType Directory -Force -Path (Join-Path $stage '.cscode-install') | Out-Null
    Set-Content -LiteralPath (Join-Path $stage '.cscode-install\info') -Value "version`t$Version`napp-version`t$appVersion`ninstalled`t$((Get-Date).ToUniversalTime().ToString('s'))Z"

    $target = Join-Path $Root $Version
    if (Test-Path -LiteralPath $target) {
        if (Test-InUse $target) { throw "CsCode $Version is running: quit it first (cscode --quit --all)" }
        Remove-Version $Version
    }
    Move-Item -LiteralPath $stage -Destination $target
} catch {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    throw
}

$prior = Read-Manifest
$priorVersions = if ($prior) { @($prior.versions | Where-Object { $_ }) } else { @() }
$priorPrevious = if ($prior) { $prior.previous } else { $null }
$current = Join-Path $Root 'current'
$before = $null
if (Test-Path -LiteralPath $current) {
    $before = Split-Path -Leaf ([string] @((Get-Item -LiteralPath $current).Target)[0]) # a string in PowerShell 7, an array in 5.1
    (Get-Item -LiteralPath $current).Delete() # the junction only
}
New-Item -ItemType Junction -Path $current -Target $target | Out-Null
$previous = if ($before -and $before -ne $Version) { $before } elseif ($priorPrevious -and $priorPrevious -ne $Version) { $priorPrevious } else { $null }

# ------------------------------------------------------------------ launcher, PATH, Start Menu
New-Item -ItemType Directory -Force -Path $Bin | Out-Null
$launcherText = @"
@echo off
rem CsCode launcher ($Marker). Written by CsCode's tools\install.ps1, removed by its -Uninstall; a reinstall replaces it.
setlocal
if not defined DOTNET_ROOT for /f "delims=" %%d in ('where dotnet 2^>nul') do if not defined DOTNET_ROOT set "DOTNET_ROOT=%%~dpd"
if defined DOTNET_ROOT if "%DOTNET_ROOT:~-1%"=="\" set "DOTNET_ROOT=%DOTNET_ROOT:~0,-1%"
set "PATH=%PATH%;%USERPROFILE%\.dotnet\tools"
"$current\cscode.exe" %*
"@
Set-Content -LiteralPath $Launcher -Value $launcherText -Encoding ascii
$addedPath = [bool]($prior -and $prior.addedPath)
if (-not (((Get-UserPath) -split ';') -contains $Bin)) {
    $userPath = Get-UserPath
    Set-UserPath ((@($userPath, $Bin) | Where-Object { $_ }) -join ';')
    $addedPath = $true
}
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = Join-Path $current 'cscode.exe'
$link.WorkingDirectory = $env:USERPROFILE
$link.IconLocation = (Join-Path $current 'cscode.exe') + ',0'
$link.Description = 'CsCode code editor'
$link.Save()

# ------------------------------------------------------------------ keep the installed and the previous version; the manifest
$kept = @($Version) + @($previous | Where-Object { $_ })
foreach ($v in $priorVersions) {
    if ($kept -contains $v) { continue }
    if (Test-InUse (Join-Path $Root $v)) { Write-Warning "CsCode $v is still running: kept"; $kept += $v } else { Remove-Version $v }
}
[ordered]@{ format = 1; current = $Version; previous = $previous; versions = $kept; addedPath = $addedPath; files = @($Launcher, $Shortcut) } |
    ConvertTo-Json | Set-Content -LiteralPath $ManifestPath

Write-Output "Installed CsCode $Version (app $appVersion) into $target."
Write-Output "  Start Menu: $Shortcut"
Write-Output "  Command line: cscode (open a new terminal: $Bin was added to your PATH)"
if ($previous) { Write-Output "  Previous version kept for rollback: $previous" }
Write-Output "  Uninstall: pwsh `"$target\install.ps1`" -Uninstall"
