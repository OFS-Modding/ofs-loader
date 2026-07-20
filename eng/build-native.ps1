param(
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ZigPath)) {
    $bundled = Join-Path $repository "tools\zig\0.16.0\zig.exe"
    if (Test-Path $bundled) {
        $ZigPath = $bundled
    }
    else {
        $command = Get-Command zig -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            throw "Zig 0.16.0 was not found. Pass -ZigPath or install it on PATH."
        }
        $ZigPath = $command.Source
    }
}

$outputDirectory = Join-Path $repository "artifacts\bootstrap"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$arguments = @(
    "cc",
    "-target", "x86_64-windows",
    "-shared",
    "-nostdlib",
    "-isystem", (Join-Path (Split-Path $ZigPath) "lib\include"),
    "-isystem", (Join-Path (Split-Path $ZigPath) "lib\libc\include\any-windows-any"),
    "-I", (Join-Path $repository "third_party\minhook\include"),
    "-I", (Join-Path $repository "third_party\minhook\src"),
    "-I", (Join-Path $repository "third_party\minhook\src\hde"),
    "-O2", "-Wall", "-Wextra", "-Werror",
    (Join-Path $repository "native\OFS.Bootstrap\version_proxy.c"),
    (Join-Path $repository "third_party\minhook\src\buffer.c"),
    (Join-Path $repository "third_party\minhook\src\hook.c"),
    (Join-Path $repository "third_party\minhook\src\trampoline.c"),
    (Join-Path $repository "third_party\minhook\src\hde\hde64.c"),
    (Join-Path $repository "native\OFS.Bootstrap\version.def"),
    "-lkernel32",
    "-o", (Join-Path $outputDirectory "version.dll")
)

& $ZigPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Native bootstrap build failed with exit code $LASTEXITCODE."
}

Write-Host "Native bootstrap: $outputDirectory\version.dll"
