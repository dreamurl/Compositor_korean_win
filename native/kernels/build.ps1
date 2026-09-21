<#
.SYNOPSIS
    Builds upstream Compositor's pixel kernels with MSVC.

.DESCRIPTION
    Produces the same objects twice over:

      static/compositor_kernels.lib   linked straight into the NativeAOT executable, so the
                                      shipped binary carries no kernel DLL beside it
      shared/compositor_kernels.dll   loaded at run time by the test run, which is plain CoreCLR

    This calls cl and lib rather than going through CMake. The kernel build is nine C files with no
    dependencies and no configuration, and CMake's Visual Studio generators have to be named by
    version — which breaks every time the CI image moves to a newer Visual Studio. vswhere always
    reports whatever is actually installed.
#>

[CmdletBinding()]
param(
    [string] $OutputDirectory = "build/kernels"
)

$ErrorActionPreference = "Stop"

$kernels = $PSScriptRoot
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory }
          else { Join-Path (Get-Location) $OutputDirectory }

# --- Find the C++ toolchain -------------------------------------------------------------------

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found at $vswhere" }

$installation = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $installation) { throw "no Visual Studio installation with the C++ tools was found" }

$vcvars = Join-Path $installation "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

Write-Host "Toolchain: $installation"

# vcvars64.bat only sets variables in its own shell, so run it, dump the environment it produced
# and copy that into this process.
& cmd.exe /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
    }
}

# --- Compile ----------------------------------------------------------------------------------

$objects = Join-Path $output "obj"
$static = Join-Path $output "static"
$shared = Join-Path $output "shared"
foreach ($directory in @($objects, $static, $shared)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

# The eight upstream files, verbatim, plus this port's own shim.
$sources = @(
    "AdjustPixels.c"
    "BrushPixels.c"
    "ContentFill.c"
    "HealPixels.c"
    "LensPixels.c"
    "LevelsPixels.c"
    "NoisePixels.c"
    "WandPixels.c"
    "shim/KernelsShim.c"
) | ForEach-Object { Join-Path $kernels $_ }

foreach ($source in $sources) {
    if (-not (Test-Path $source)) { throw "missing kernel source: $source" }
}

# /fp:precise, not /fp:fast: these kernels' output gets compared against the macOS build, and
# reassociating floating point would put those comparisons permanently slightly off.
# /MT matches NativeAOT, which links the static CRT. Warnings stay warnings: these files
# are upstream's, and silencing one would mean diverging from a source meant to be
# refreshable.
#
# _USE_MATH_DEFINES is what MSVC wants before <math.h> will define M_PI, which HealPixels.c uses.
# Setting it here rather than adding an #include to that file keeps the eight upstream sources
# byte-identical to their originals, so they can be refreshed with a copy.
$compile = @(
    "/c", "/nologo", "/O2", "/W3", "/fp:precise", "/MT", "/std:c11", "/utf-8"
    "/D_USE_MATH_DEFINES", "/D_CRT_SECURE_NO_WARNINGS"
    "/I", $kernels
    "/Fo:$objects\"
) + $sources

Write-Host "cl $($sources.Count) sources"
& cl.exe @compile
if ($LASTEXITCODE -ne 0) { throw "cl failed with $LASTEXITCODE" }

$objectFiles = Get-ChildItem $objects -Filter *.obj | ForEach-Object { $_.FullName }

& lib.exe /nologo "/OUT:$static\compositor_kernels.lib" @objectFiles
if ($LASTEXITCODE -ne 0) { throw "lib failed with $LASTEXITCODE" }

& link.exe /DLL /nologo "/DEF:$kernels\kernels.def" `
    "/OUT:$shared\compositor_kernels.dll" "/IMPLIB:$shared\compositor_kernels.lib" @objectFiles
if ($LASTEXITCODE -ne 0) { throw "link failed with $LASTEXITCODE" }

Write-Host ""
Get-ChildItem $static, $shared -File | ForEach-Object {
    "{0,-40} {1,10:N0} bytes" -f $_.FullName.Substring($output.Length + 1), $_.Length | Write-Host
}
