<#
  Partition GUID checks in the GUI: an ext4 partition typed "Microsoft basic
  data" must raise a warning, and "Imposta tipo «Linux»" must fix the table.
#>
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $root 'tests\work'
$app = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64\WinExt4.exe'
$cli = Join-Path $root 'native\build\Release\e4cli.exe'
$uia = Join-Path $PSScriptRoot 'uia.ps1'
$img = Join-Path $work 'wrongtype-gpt.img'
Copy-Item (Join-Path $root 'tests\img\disk-gpt.img') $img -Force
$env:MSYS_NO_PATHCONV = 1
& $cli setpart $img 1 type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function WaitUi($procId, $pattern, $seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $all = ((& $uia -ProcId $procId -Action text) + (& $uia -ProcId $procId -Action list)) -join "`n"
        if ($all -match $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$env:__COMPAT_LAYER = 'RunAsInvoker'
$p = Start-Process $app -ArgumentList "`"$img`"" -PassThru
Check (WaitUi $p.Id '· aperto') 'image opened'
Check (WaitUi $p.Id 'Microsoft basic data') 'warning about the partition type shown'
& (Join-Path $PSScriptRoot 'shot.ps1') -Out (Join-Path $work 'p2-warning.png') -ProcId $p.Id | Out-Null
& $uia -ProcId $p.Id -Action click -Name 'Imposta tipo' | Out-Null
Check (WaitUi $p.Id 'DIALOG: Tipo di partizione') 'confirmation asked'
& $uia -ProcId $p.Id -Action click -Name 'OK' | Out-Null
Check (WaitUi $p.Id 'Tipo di partizione aggiornato|Aperto ext4') 'type changed and volume reopened'
Start-Sleep 2
$t = (& $uia -ProcId $p.Id -Action text) -join "`n"
Check ($t -notmatch 'Microsoft basic data') 'warning gone after the fix'
Check ($t -match '· aperto') 'volume open again'
Stop-Process -Id $p.Id
$w = $img -replace '^C:', '/mnt/c' -replace '\\', '/'
$d = wsl -d Debian -- sfdisk -d $w
Check ("$d" -match 'type=0FC63DAF-8483-4772-8E79-3D69D8477DE4') 'sfdisk sees the Linux filesystem type'
$v = wsl -d Debian -- sfdisk -V $w
Check ("$v" -match 'No errors') 'sfdisk -V: no errors'
"FAILS=$fails"
