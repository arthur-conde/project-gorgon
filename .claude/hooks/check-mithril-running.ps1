#!/usr/bin/env pwsh
# PreToolUse hook: warn (or block) when `dotnet build|test|publish|pack`
# is run while Mithril.Shell.exe is running.
#
# Hosts: Claude Code (exit codes + stderr) and Cursor (JSON on stdout; loads
# this via .claude/settings.json but does not set $CLAUDE_PROJECT_DIR).
#
# Memory ref: mithril_build_file_lock_silent.md.

$ErrorActionPreference = 'Stop'

function Test-CursorHookHost {
    # Cursor loads .claude/settings.json hooks but does not set CLAUDE_PROJECT_DIR.
    return -not $env:CLAUDE_PROJECT_DIR
}

function Write-HookAllow {
    if (Test-CursorHookHost) {
        [Console]::Out.WriteLine('{"permission":"allow"}')
    }
}

function Write-HookDeny {
    param([Parameter(Mandatory)][string]$Message)
    if (Test-CursorHookHost) {
        $body = @{ permission = 'deny'; agent_message = $Message }
        [Console]::Out.WriteLine(($body | ConvertTo-Json -Compress))
    } else {
        [Console]::Error.WriteLine($Message)
    }
}

function Exit-HookAllow {
    Write-HookAllow
    exit 0
}

function Exit-HookDeny {
    param([Parameter(Mandatory)][string]$Message)
    Write-HookDeny -Message $Message
    exit 2
}

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
}
catch {
    Exit-HookAllow
}

# Bash / PowerShell (Claude) or Shell (Cursor preToolUse).
if ($payload.tool_name -and
    $payload.tool_name -ne 'Bash' -and
    $payload.tool_name -ne 'PowerShell' -and
    $payload.tool_name -ne 'Shell') {
    Exit-HookAllow
}

$cmd = $payload.tool_input.command
if (-not $cmd) { $cmd = $payload.command }
if (-not $cmd) { Exit-HookAllow }

if ($cmd -notmatch '(?:^|[\s;&|])dotnet\s+(build|test|publish|pack)\b') { Exit-HookAllow }

$running = Get-Process Mithril.Shell -ErrorAction SilentlyContinue
if (-not $running) { Exit-HookAllow }

Add-Type -ErrorAction SilentlyContinue -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MithrilCheckNative {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);
}
"@

$anyDebugged = $false
foreach ($p in $running) {
    try {
        $isDbg = $false
        if ([MithrilCheckNative]::CheckRemoteDebuggerPresent($p.Handle, [ref]$isDbg) -and $isDbg) {
            $anyDebugged = $true
            break
        }
    } catch {
        # Treat as not-debugged so the block still protects.
    }
}

$pidList = ($running | ForEach-Object { $_.Id }) -join ', '

if ($anyDebugged) {
    [Console]::Error.WriteLine(
        "[mithril-check] Mithril.Shell.exe (PID $pidList) has a debugger attached — allowing the build, but the module-DLL copy step will MSB3026/27. The running shell keeps its loaded DLLs; the copy failure only affects what a *next* launch would pick up."
    )
    Exit-HookAllow
}

$denyMsg = @(
    "[mithril-check] Mithril.Shell.exe is running (PID $pidList), no debugger attached."
    "[mithril-check] Module DLL copy step will silently fail (MSB3026/27); src/Mithril.Shell/*/modules/ would be left stale."
    "[mithril-check] Close the shell window or 'Stop-Process -Name Mithril.Shell', then retry."
) -join ' '

if (Test-CursorHookHost) {
    Exit-HookDeny -Message $denyMsg
}

[Console]::Error.WriteLine("[mithril-check] Mithril.Shell.exe is running (PID $pidList), no debugger attached.")
[Console]::Error.WriteLine("[mithril-check] Module DLL copy step will silently fail (MSB3026/27); src/Mithril.Shell/*/modules/ would be left stale.")
[Console]::Error.WriteLine("[mithril-check] Close the shell window or 'Stop-Process -Name Mithril.Shell', then retry. (If you're actively debugging via VS/Rider and this fires anyway, the debugger may not have attached yet — wait for it to hit a breakpoint or call Debugger.Launch().)")
exit 2
