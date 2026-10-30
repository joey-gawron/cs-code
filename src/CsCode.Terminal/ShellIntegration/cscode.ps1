# CsCode shell integration for PowerShell: OSC 633 marks around the prompt (A, B), the command line (E) and its output (C), its result
# (D) and the working directory (P;Cwd). CsCode starts pwsh as `pwsh -NoExit -Command ". '<this file>'"`, which runs after pwsh loaded
# the user's profile: the profile is read as usual and never changed.
if ($Global:__CsCodeState) { return }

$Global:__CsCodeState = @{
    Nonce          = $env:CSCODE_NONCE
    OriginalPrompt = $function:prompt
    LastHistoryId  = -1
    Executing      = $false
    HasPSReadLine  = $false
}
Remove-Item Env:CSCODE_NONCE -ErrorAction SilentlyContinue
Remove-Item Env:CSCODE_INJECTION -ErrorAction SilentlyContinue

# A value in an OSC 633 sequence: backslashes doubled, ';' and control characters as \xHH, so it can never end the sequence early.
function Global:__CsCode-Escape([string]$Value) {
    $sb = [System.Text.StringBuilder]::new()
    foreach ($c in $Value.ToCharArray()) {
        $n = [int]$c
        if ($c -eq '\') { [void]$sb.Append('\\') }
        elseif ($c -eq ';') { [void]$sb.Append('\x3b') }
        elseif ($n -lt 0x20 -or $n -eq 0x7f) { [void]$sb.Append(('\x{0:x2}' -f $n)) }
        else { [void]$sb.Append($c) }
    }
    $sb.ToString()
}

function Global:prompt {
    # $? first: everything after this line changes it
    $succeeded = $global:?
    Set-StrictMode -Off
    $esc = [char]0x1b
    $bel = [char]0x07
    $state = $Global:__CsCodeState
    $last = Get-History -Count 1
    $result = ''
    if ($state.LastHistoryId -ne -1 -and ($state.Executing -or -not $state.HasPSReadLine)) {
        $state.Executing = $false
        if ($last -and $last.Id -ne $state.LastHistoryId) {
            if (-not $state.HasPSReadLine) {
                # without PSReadLine nobody reported the line or the start of its output: do it now
                $result += "$esc]633;E;$(__CsCode-Escape $last.CommandLine);$($state.Nonce)$bel$esc]633;C$bel"
            }
            # PowerShell knows success or failure of the last pipeline, not an exit code: 0 or 1 (a native program's own code when it failed)
            $code = if ($succeeded) { 0 } elseif ($global:LASTEXITCODE -is [int] -and $global:LASTEXITCODE -ne 0) { $global:LASTEXITCODE } else { 1 }
            $result += "$esc]633;D;$code$bel"
        }
    }
    if ($PWD.Provider.Name -eq 'FileSystem') { $result += "$esc]633;P;Cwd=$(__CsCode-Escape $PWD.ProviderPath)$bel" }
    $result += "$esc]633;A$bel"
    # the user's prompt sees $? as it was
    if (-not $succeeded) { Write-Error 'failure' -ErrorAction Ignore }
    $result += $state.OriginalPrompt.Invoke()
    $result += "$esc]633;B$bel"
    $state.LastHistoryId = if ($last) { $last.Id } else { 0 }
    $result
}

# With PSReadLine the line is known the moment Enter is pressed: E and C then, before the command's output.
if (Get-Module -Name PSReadLine) {
    $Global:__CsCodeState.HasPSReadLine = $true
    $Global:__CsCodeState.OriginalReadLine = $function:PSConsoleHostReadLine
    function Global:PSConsoleHostReadLine {
        $line = $Global:__CsCodeState.OriginalReadLine.Invoke()
        $Global:__CsCodeState.Executing = $true
        [Console]::Write("$([char]0x1b)]633;E;$(__CsCode-Escape $line);$($Global:__CsCodeState.Nonce)$([char]0x07)$([char]0x1b)]633;C$([char]0x07)")
        $line
    }
}
