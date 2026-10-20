# Builds the app and the one-file installer with the C# compiler that ships with Windows (.NET Framework 4.8).
#   powershell -ExecutionPolicy Bypass -File build.ps1          -> bin\FixMultipleArtists.exe, bin\FixMultipleArtists-Setup.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -test    -> also bin\EngineTests.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$bin = Join-Path $root 'bin'
$dll = Join-Path $root 'lib\TagLibSharp.dll'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item $dll $bin -Force

$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName

& $csc /nologo /target:winexe /optimize+ /out:"$bin\FixMultipleArtists.exe" @refs "/r:$dll" @src
if ($LASTEXITCODE) { throw 'App build failed' }

# The installer carries the app and TagLibSharp.dll inside itself.
& $csc /nologo /target:winexe /optimize+ /out:"$bin\FixMultipleArtists-Setup.exe" @refs `
    "/win32manifest:$root\installer\Setup.manifest" `
    "/resource:$bin\FixMultipleArtists.exe,FixMultipleArtists.exe" "/resource:$dll,TagLibSharp.dll" `
    "$root\installer\Setup.cs"
if ($LASTEXITCODE) { throw 'Setup build failed' }

if ($args -contains '-test') {
    $engine = "$root\src\ArtistParser.cs", "$root\src\Song.cs"
    & $csc /nologo /target:exe /out:"$bin\EngineTests.exe" @refs "/r:$dll" "$root\tests\EngineTests.cs" @engine
    if ($LASTEXITCODE) { throw 'Test build failed' }
}
Write-Host "Built $bin\FixMultipleArtists.exe and $bin\FixMultipleArtists-Setup.exe"
