# build-exe.ps1 -- Compile Scout Loader into a self-contained exe using the
# .NET Framework C# compiler that ships with Windows (no SDK / Node needed).
#
# Only the language-neutral engine (overlay-engine.js) is embedded, so the exe
# is a PURE loader. Dictionaries are NOT embedded -- they live as external
# language packs (dictionary.<lang>.json) next to the exe, so anyone can add a
# new language without recompiling. The Scout icon (scout.ico) is applied.
#
# Deliverable = "Scout Loader.exe"  +  one or more  dictionary.<lang>.json.
# Output runs on any machine with .NET Framework 4.6+ (built into Win10/11).
#
# Usage:  powershell -ExecutionPolicy Bypass -File build-exe.ps1

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Locate csc.exe (prefer 64-bit framework dir).
$candidates = @(
  "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
  throw "csc.exe (.NET Framework 4.x compiler) not found. Requires .NET Framework 4.6+ (default on Win10/11)."
}

$src    = Join-Path $here "ScoutZh.cs"
$engine = Join-Path $here "overlay-engine.js"
$icon   = Join-Path $here "scout.ico"
$out    = Join-Path $here "Scout Loader.exe"

foreach ($f in @($src, $engine, $icon)) {
  if (-not (Test-Path $f)) { throw "Missing required file: $f" }
}

Write-Host "Compiler: $csc"
Write-Host "Compiling Scout Loader.exe (engine embedded, dictionaries external) ..."

# Embed ONLY the engine. No dictionary is baked in.
$cscArgs = @(
  "/nologo", "/optimize+", "/target:winexe", "/platform:anycpu",
  "/reference:System.Web.Extensions.dll",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Drawing.dll",
  "/resource:$engine,overlay-engine.js",
  "/win32icon:$icon",
  "/out:$out",
  $src
)
& $csc @cscArgs

if ($LASTEXITCODE -ne 0) { throw "Compile failed (csc exit code $LASTEXITCODE)" }

# List the language packs shipping alongside the exe.
$packs = Get-ChildItem -Path $here -Filter "dictionary.*.json" |
  ForEach-Object { $_.Name -replace '^dictionary\.', '' -replace '\.json$', '' }

Write-Host ""
Write-Host "OK -> $out"
if ($packs) { Write-Host ("Language packs found: " + ($packs -join ", ")) }
else { Write-Host "WARNING: no dictionary.<lang>.json language pack found next to the exe." }
Write-Host ""
Write-Host 'Run:  & ".\Scout Loader.exe"                (default lang zh-CN, inject + stay resident)'
Write-Host '      & ".\Scout Loader.exe" --lang ja-JP    (load dictionary.ja-JP.json)'
Write-Host '      & ".\Scout Loader.exe" --once          (inject once and exit)'
Write-Host ""
Write-Host "Deliverable = Scout Loader.exe + dictionary.<lang>.json (the exe is language-neutral)."
