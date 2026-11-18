#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the sherpa-onnx native DLLs with the DirectML (graphics card) execution provider.

.DESCRIPTION
    The org.k2fsa.sherpa.onnx NuGet package ships CPU-only Windows DLLs. This script builds the
    same sherpa-onnx version from source with -DSHERPA_ONNX_ENABLE_DIRECTML=ON and collects every
    DLL the app needs into one folder; the release workflow copies them over the published app
    (spec 04-dictation-settings §9.5).

    sherpa-onnx's cmake/onnxruntime-win-x64-directml.cmake downloads fixed, old versions of
    ONNX Runtime DirectML and DirectML. The script downloads newer NuGet packages itself and points
    the URL/HASH lines of that file at the local files (with their SHA-256).

    Needs Windows x64, Git, CMake 3.25+ and Visual Studio (Build Tools) with the C++ workload and a
    Windows 10/11 SDK; the Visual Studio generator is required (the cmake file checks the -A x64
    platform). dumpbin (from a Developer PowerShell or ilammy/msvc-dev-cmd) enables the dependency
    check at the end.

.EXAMPLE
    pwsh windows/native/build-directml.ps1
    pwsh windows/native/build-directml.ps1 -SherpaVersion 2.0.1 -OutDir C:\temp\directml
#>
[CmdletBinding()]
param(
    # Must match the org.k2fsa.sherpa.onnx package version of MeetingAssistant.Speech (the C# binding).
    [string]$SherpaVersion = '2.0.1',
    [string]$OnnxRuntimeVersion = '1.24.4',
    [string]$DirectMLVersion = '1.15.4',
    # Sources, packages, build and install trees.
    [string]$WorkDir = (Join-Path $PSScriptRoot 'build'),
    # Receives the DLLs (replaced on every run).
    [string]$OutDir = (Join-Path $PSScriptRoot 'directml')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $true

if (-not $IsWindows) { throw 'Builds Windows x64 DLLs: run it on Windows.' }
foreach ($tool in 'git', 'cmake') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on PATH." }
}

$WorkDir = [IO.Path]::GetFullPath($WorkDir)
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# A preinstalled ONNX Runtime would win over the DirectML one (cmake/onnxruntime.cmake).
Remove-Item Env:SHERPA_ONNXRUNTIME_LIB_DIR, Env:SHERPA_ONNXRUNTIME_INCLUDE_DIR -ErrorAction SilentlyContinue

# 1. Sources of the tagged release.
$src = Join-Path $WorkDir "sherpa-onnx-$SherpaVersion"
if (-not (Test-Path (Join-Path $src 'CMakeLists.txt'))) {
    if (Test-Path $src) { Remove-Item $src -Recurse -Force }
    Write-Host "Cloning sherpa-onnx v$SherpaVersion"
    git -c advice.detachedHead=false clone --depth 1 --branch "v$SherpaVersion" https://github.com/k2-fsa/sherpa-onnx.git $src
}
else {
    # Undo an earlier run's edit of the cmake file.
    git -C $src checkout -- cmake/onnxruntime-win-x64-directml.cmake
}

# 2. ONNX Runtime DirectML and DirectML from nuget.org.
$packages = Join-Path $WorkDir 'packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null
function Get-NuGetPackage([string]$Id, [string]$Version) {
    $file = Join-Path $packages "$($Id.ToLowerInvariant()).$Version.nupkg"
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $Id $Version"
        $tmp = "$file.download"
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/$Id/$Version" -OutFile $tmp
        Move-Item $tmp $file -Force
    }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
    [pscustomobject]@{ Path = ($file -replace '\\', '/'); Hash = $hash }
}
$ort = Get-NuGetPackage 'Microsoft.ML.OnnxRuntime.DirectML' $OnnxRuntimeVersion
$dml = Get-NuGetPackage 'Microsoft.AI.DirectML' $DirectMLVersion
Write-Host "onnxruntime $OnnxRuntimeVersion  SHA256=$($ort.Hash)"
Write-Host "DirectML    $DirectMLVersion  SHA256=$($dml.Hash)"

# 3. Point the cmake file at the local packages.
$cmakeFile = Join-Path $src 'cmake/onnxruntime-win-x64-directml.cmake'
$text = Get-Content -Raw -Path $cmakeFile
function Set-CMakeValue([string]$Text, [string]$Pattern, [string]$Value) {
    $regex = [regex]::new($Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    $count = $regex.Matches($Text).Count
    if ($count -ne 1) { throw "Expected one line matching '$Pattern' in onnxruntime-win-x64-directml.cmake, found $count. Check the script against this sherpa-onnx version." }
    return $regex.Replace($Text, { param($m) $m.Groups[1].Value + '"' + $Value + '")' })
}
# Only the https lines; the "${f}" lines inside the local-file loops stay.
$text = Set-CMakeValue $text '^(\s*set\(onnxruntime_URL\s+)"https?://[^"]*"\)' $ort.Path
$text = Set-CMakeValue $text '^(\s*set\(onnxruntime_HASH\s+)"SHA256=[0-9a-fA-F]+"\)' "SHA256=$($ort.Hash)"
$text = Set-CMakeValue $text '^(\s*set\(directml_URL\s+)"https?://[^"]*"\)' $dml.Path
$text = Set-CMakeValue $text '^(\s*set\(directml_HASH\s+)"SHA256=[0-9a-fA-F]+"\)' "SHA256=$($dml.Hash)"
Set-Content -Path $cmakeFile -Value $text -NoNewline

# 4. Configure, build and install (static CRT, the sherpa-onnx default, so no VC++ runtime is needed).
#    C API, TTS and speaker diarization stay on: the C# binding P/Invokes all of their exports.
$build = Join-Path $WorkDir "build-$SherpaVersion"
$install = Join-Path $WorkDir "install-$SherpaVersion"
if (Test-Path $install) { Remove-Item $install -Recurse -Force }
cmake -S $src -B $build -A x64 `
    -DCMAKE_BUILD_TYPE=Release `
    "-DCMAKE_INSTALL_PREFIX=$install" `
    -DBUILD_SHARED_LIBS=ON `
    -DSHERPA_ONNX_ENABLE_DIRECTML=ON `
    -DSHERPA_ONNX_ENABLE_C_API=ON `
    -DSHERPA_ONNX_ENABLE_TTS=ON `
    -DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=ON `
    -DSHERPA_ONNX_ENABLE_BINARY=OFF `
    -DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF `
    -DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF `
    -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF `
    -DSHERPA_ONNX_ENABLE_PYTHON=OFF `
    -DSHERPA_ONNX_ENABLE_TESTS=OFF
cmake --build $build --config Release --parallel
cmake --install $build --config Release

# 5. Collect the DLLs (the C API installs into lib, ONNX Runtime and DirectML into bin and lib).
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dlls = foreach ($dir in 'bin', 'lib') {
    $path = Join-Path $install $dir
    if (Test-Path $path) { Get-ChildItem -Path $path -Filter '*.dll' -File }
}
foreach ($dll in $dlls) {
    if ($dll.Name -like '*.Debug.dll') { continue }       # DirectML.Debug.dll (debug layer)
    $target = Join-Path $OutDir $dll.Name
    if (-not (Test-Path $target)) { Copy-Item $dll.FullName $target }
}

# 6. Checks.
foreach ($name in 'sherpa-onnx-c-api.dll', 'onnxruntime.dll', 'DirectML.dll') {
    if (-not (Test-Path (Join-Path $OutDir $name))) { throw "$name is missing in $OutDir" }
}
function Test-Contains([string]$Path, [string]$Text) {
    [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Path)).Contains($Text)
}
if (-not (Test-Contains (Join-Path $OutDir 'onnxruntime.dll') 'OrtSessionOptionsAppendExecutionProvider_DML')) {
    throw 'onnxruntime.dll has no DirectML provider.'
}
$sherpaDlls = Get-ChildItem -Path $OutDir -Filter '*.dll' | Where-Object { $_.Name -notin 'onnxruntime.dll', 'DirectML.dll' }
if (-not ($sherpaDlls | Where-Object { Test-Contains $_.FullName 'Failed to enable DirectML' })) {
    throw 'sherpa-onnx was built without DirectML (no DirectML code path found).'
}
if ($sherpaDlls | Where-Object { Test-Contains $_.FullName 'DirectML is for Windows only' }) {
    throw 'sherpa-onnx contains the CPU-only DirectML fallback: SHERPA_ONNX_ENABLE_DIRECTML did not apply.'
}

# Every DLL a collected DLL imports must be in the folder or part of Windows.
if (Get-Command dumpbin -ErrorAction SilentlyContinue) {
    $system = Join-Path $env:SystemRoot 'System32'
    $missing = @()
    foreach ($dll in Get-ChildItem -Path $OutDir -Filter '*.dll') {
        $deps = dumpbin /nologo /dependents $dll.FullName | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[\w.\-]+\.dll$' }
        foreach ($dep in $deps) {
            if ($dep -like 'api-ms-win-*' -or $dep -like 'ext-ms-*') { continue }
            if (Test-Path (Join-Path $OutDir $dep)) { continue }
            if (Test-Path (Join-Path $system $dep)) { continue }
            $missing += "$($dll.Name) -> $dep"
        }
    }
    if ($missing) { throw "Unresolved DLL imports:`n$($missing -join "`n")" }
}
else {
    Write-Warning 'dumpbin not found (run from a Developer PowerShell): DLL imports not checked.'
}

@(
    "sherpa-onnx $SherpaVersion (DirectML)"
    "Microsoft.ML.OnnxRuntime.DirectML $OnnxRuntimeVersion"
    "Microsoft.AI.DirectML $DirectMLVersion"
) | Set-Content -Path (Join-Path $OutDir 'versions.txt')

Write-Host "DirectML DLLs in ${OutDir}:"
Get-ChildItem -Path $OutDir | ForEach-Object { Write-Host ("  {0,-40} {1,12:N0}" -f $_.Name, $_.Length) }
