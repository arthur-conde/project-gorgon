#!/usr/bin/env pwsh
# PreToolUse hook: warn (or block) when `dotnet build|test|publish|pack`
# is run while Mithril.Shell.exe is running.
#
# Why: a running shell holds open handles to module DLLs in
# src/Mithril.Shell/<config>/modules/. The post-build copy step (see
# Directory.Build.targets) fails with MSB3026/27 — silently — so bin/Debug
# updates but the modules folder ends up stale. Tests pass, build prints
# "succeeded", and the user runs the shell against yesterday's code.
#
# Behaviour:
# - No Mithril.Shell.exe running                   → exit 0 (silent)
# - Mithril.Shell.exe running, NO debugger attached → exit 2 (block, ask
#                                                      the user to stop it)
# - Mithril.Shell.exe running, debugger attached    → exit 0 + stderr warning
#                                                      (this is a debug
#                                                      session; the user is
#                                                      on purpose, but the
#                                                      modules-folder copy
#                                                      will still fail)
#
# Memory ref: mithril_build_file_lock_silent.md.

$ErrorActionPreference = 'Stop'

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
}
catch {
    exit 0  # malformed payload — don't block
}

# Match either Bash or PowerShell tool calls invoking dotnet build/test/publish/pack.
if ($payload.tool_name -ne 'Bash' -and $payload.tool_name -ne 'PowerShell') { exit 0 }

$cmd = $payload.tool_input.command
if (-not $cmd) { exit 0 }

if ($cmd -notmatch '(?:^|[\s;&|])dotnet\s+(build|test|publish|pack)\b') { exit 0 }

$running = Get-Process Mithril.Shell -ErrorAction SilentlyContinue
if (-not $running) { exit 0 }

# Detect whether ANY Mithril.Shell process is being debugged.
# Win32 CheckRemoteDebuggerPresent reports both user-mode debuggers (VS,
# Rider, dnSpy, WinDbg) and kernel debuggers; that's the canonical signal.
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
        # Process.Handle requires the same-or-higher integrity level; this
        # runs as the user, same as the shell, so it's fine.
        if ([MithrilCheckNative]::CheckRemoteDebuggerPresent($p.Handle, [ref]$isDbg) -and $isDbg) {
            $anyDebugged = $true
            break
        }
    } catch {
        # Handle-acquisition failure (rare; e.g. elevated shell) — fall
        # through and treat as not-debugged so the block still protects.
    }
}

$pidList = ($running | ForEach-Object { $_.Id }) -join ', '

if ($anyDebugged) {
    # User is actively debugging — DO NOT block. Surface a one-line note so
    # neither I nor they are surprised when the modules-folder copy step
    # fails with MSB3026/27 (that's expected during a debug session and
    # doesn't matter for what's already loaded in the running shell).
    [Console]::Error.WriteLine("[mithril-check] Mithril.Shell.exe (PID $pidList) has a debugger attached — allowing the build, but the module-DLL copy step will MSB3026/27. The running shell keeps its loaded DLLs; the copy failure only affects what a *next* launch would pick up.")
    exit 0
}

[Console]::Error.WriteLine("[mithril-check] Mithril.Shell.exe is running (PID $pidList), no debugger attached.")
[Console]::Error.WriteLine("[mithril-check] Module DLL copy step will silently fail (MSB3026/27); src/Mithril.Shell/*/modules/ would be left stale.")
[Console]::Error.WriteLine("[mithril-check] Close the shell window or 'Stop-Process -Name Mithril.Shell', then retry. (If you're actively debugging via VS/Rider and this fires anyway, the debugger may not have attached yet — wait for it to hit a breakpoint or call Debugger.Launch().)")
exit 2
