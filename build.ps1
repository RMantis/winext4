<#
  Builds WinExt4 into dist\WinExt4.
    .\build.ps1                  framework-dependent (needs the .NET 8 Desktop Runtime)
    .\build.ps1 -SelfContained   single self-contained WinExt4.exe (no runtime needed)
  Requires Visual Studio 2022 (C++ workload) and the .NET 8 SDK. WinFsp must be
  installed to build (for winfsp-msil.dll) and to mount drives at runtime.
#>
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& cmd /c "`"$root\native\build.cmd`" Release"
if ($LASTEXITCODE) { throw "native build failed" }

$out = Join-Path $root 'dist\WinExt4'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$args = @('publish', "$root\src\WinExt4\WinExt4.csproj", '-c', 'Release', '-o', $out, '-nologo')
if ($SelfContained) {
    $args += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
}
& dotnet @args
if ($LASTEXITCODE) { throw "dotnet publish failed" }
Get-ChildItem $out | Select-Object Name, Length
