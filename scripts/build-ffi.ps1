[CmdletBinding()]
param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) {
    $zigVersion = '0.17.1'
    $toolRoot = Join-Path $projectRoot '.tools'
    $zigRoot = Join-Path $toolRoot "zig-x86_64-windows-$zigVersion"
    $CompilerPath = Join-Path $zigRoot 'zig.exe'
    if (-not (Test-Path -LiteralPath $CompilerPath)) {
        [IO.Directory]::CreateDirectory($toolRoot) | Out-Null
        $index = Invoke-RestMethod -Uri 'https://ziglang.org/download/index.json'
        $release = $index.$zigVersion.'x86_64-windows'
        if (-not $release -or $release.tarball -notlike 'https://ziglang.org/download/*') { throw 'Official Zig release metadata is unavailable.' }
        $archive = Join-Path $toolRoot "zig-$zigVersion.zip"
        if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -UseBasicParsing -Uri $release.tarball -OutFile $archive }
        $hash = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($archive)
        try { $actualHash = ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose(); $hash.Dispose() }
        if ($actualHash -ne $release.shasum) { throw 'Zig archive checksum mismatch; archive was not extracted.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archiveFile = [IO.Compression.ZipFile]::OpenRead($archive)
        $allowedRoot = [IO.Path]::GetFullPath($toolRoot) + [IO.Path]::DirectorySeparatorChar
        try {
            foreach ($entry in $archiveFile.Entries) {
                $target = [IO.Path]::GetFullPath((Join-Path $toolRoot $entry.FullName))
                if (-not $target.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry is outside the compiler cache.' }
                if ($entry.Name.Length -eq 0) { [IO.Directory]::CreateDirectory($target) | Out-Null }
                else { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null; [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true) }
            }
        } finally { $archiveFile.Dispose() }
    }
}
$outputDir = Join-Path $projectRoot 'ffi\bin'
[IO.Directory]::CreateDirectory($outputDir) | Out-Null
& $CompilerPath cc -shared -O2 -Wall -Wextra -Werror (Join-Path $projectRoot 'ffi\agy_desktop.c') -o (Join-Path $outputDir 'agy_desktop.dll') -lkernel32 -ladvapi32
if ($LASTEXITCODE -ne 0) { throw 'C ABI library build failed.' }
Write-Host 'Built ffi\bin\agy_desktop.dll. No desktop traces or media were recorded.'
