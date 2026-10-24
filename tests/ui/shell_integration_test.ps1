<#
  Registers WinExt4 in Explorer's "Open with" (per user), checks the keys,
  unregisters and checks that HKCU\Software\Classes is back as it was.
#>
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function Snapshot {
    foreach ($e in [WinExt4.Core.ShellIntegration]::Extensions) {
        foreach ($k in "HKCU:\Software\Classes\$e", "HKCU:\Software\Classes\SystemFileAssociations\$e") {
            if (Test-Path $k) { Get-ChildItem $k -Recurse -ErrorAction SilentlyContinue | ForEach-Object Name; $k }
        }
    }
}
$before = Snapshot
Check (-not [WinExt4.Core.ShellIntegration]::IsRegistered) 'not registered initially'
[WinExt4.Core.ShellIntegration]::Register()
Check ([WinExt4.Core.ShellIntegration]::IsRegistered) 'registered'
$cmd = (Get-ItemProperty 'HKCU:\Software\Classes\SystemFileAssociations\.img\shell\WinExt4.Open\command').'(default)'
Check ($cmd -match 'WinExt4' -and $cmd -match '%1') "context verb command: $cmd"
Check ((Get-Item 'HKCU:\Software\Classes\.img\OpenWithProgids').Property -contains 'WinExt4.DiskImage') '.img lists WinExt4 in OpenWithProgids'
$types = (Get-Item 'HKCU:\Software\Classes\Applications\WinExt4.exe\SupportedTypes').Property
Check ($types -contains '.img' -and $types -contains '.raw') "SupportedTypes: $($types -join ' ')"
[WinExt4.Core.ShellIntegration]::Unregister()
Check (-not [WinExt4.Core.ShellIntegration]::IsRegistered) 'unregistered'
Check (-not (Test-Path 'HKCU:\Software\Classes\Applications\WinExt4.exe')) 'application key removed'
$after = Snapshot
Check ((Compare-Object @($before) @($after)).Count -eq 0) 'per-extension keys back to the original state'
"FAILS=$fails"
