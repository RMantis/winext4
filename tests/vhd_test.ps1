<#
  Real-disk test: attaches ext4 VHDs (tests/make_vhd.py) as Windows disks and
  exercises the raw-partition path (\\.\HarddiskNPartitionM), volume locking,
  the WinFsp mount on a device and the GUI's disk-arrival detection.
  Must run elevated. Log: tests\work\vhd-test.log
#>
param([switch]$GuiOnly)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$work = Join-Path $root 'tests\work'
Start-Transcript (Join-Path $work 'vhd-test.log') -Force | Out-Null
$cli = Join-Path $root 'native\build\Release\e4cli.exe'
$mt = Join-Path $root 'tests\MountTest\bin\Debug\net8.0-windows\win-x64\MountTest.exe'
$app = Join-Path $root 'dist\WinExt4\WinExt4.exe'
$uia = Join-Path $PSScriptRoot 'ui\uia.ps1'
$shot = Join-Path $PSScriptRoot 'ui\shot.ps1'
$script:fails = 0

function Check($ok, $what) {
    if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ }
}
function Find-ExtPart($disk) {
    $line = & $cli disks | Where-Object { $_ -match "^disk=$disk " -and $_ -match 'ext=1' } | Select-Object -First 1
    if ($line -match 'dev=(\S+)$') { $Matches[1] }
}
function UiText($procId) { & $uia -ProcId $procId -Action text }
function WaitUi($procId, $pattern, $seconds = 20) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $all = ((UiText $procId) + (& $uia -ProcId $procId -Action list)) -join "`n"
        if ($all -match $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    "  (timeout waiting for '$pattern'); UI now:"
    & $uia -ProcId $procId -Action list
    return $false
}

$gptPath = Join-Path $work 'vhd-gpt.vhd'
$mbrPath = Join-Path $work 'vhd-mbr.vhd'
$proc = $null
try {
    if (-not $GuiOnly) {
    "== 1. GPT VHD: raw partition read/write with e4cli"
    $gpt = Mount-DiskImage -ImagePath $gptPath -PassThru | Get-Disk
    Start-Sleep 3
    "  attached as disk $($gpt.Number) ($($gpt.FriendlyName), bus $($gpt.BusType))"
    & $cli disks
    $dev = Find-ExtPart $gpt.Number
    Check ($null -ne $dev) "ext partition found on disk $($gpt.Number): $dev"
    & $cli info $dev 0
    Check ((& $cli cat $dev 0 /hello.txt) -eq 'hello from ext4') 'read hello.txt from the raw partition'

    $rnd = Join-Path $work 'rnd.bin'
    $bytes = New-Object byte[] (3MB + 777); (New-Object Random 42).NextBytes($bytes); [IO.File]::WriteAllBytes($rnd, $bytes)
    & $cli put $dev 0 $rnd / rnd.bin; Check ($LASTEXITCODE -eq 0) 'write 3 MB file (unaligned size)'
    & $cli mkdir $dev 0 / vhd-dir; Check ($LASTEXITCODE -eq 0) 'mkdir'
    & $cli mv $dev 0 / empty /vhd-dir empty-moved; Check ($LASTEXITCODE -eq 0) 'rename across directories'
    $back = Join-Path $work 'rnd-back.bin'
    cmd /c "`"$cli`" cat $dev 0 /rnd.bin > `"$back`""
    Check ((Get-FileHash $rnd).Hash -eq (Get-FileHash $back).Hash) 'read back 3 MB file identical'

    "== 2. WinFsp mount of the raw partition"
    & $mt $dev
    Check ($LASTEXITCODE -eq 0) 'MountTest on device'
    }

    "== 3. GUI: disk arrival, auto-open, write lock"
    $proc = Start-Process $app -PassThru
    Start-Sleep 6
    $mbr = Mount-DiskImage -ImagePath $mbrPath -PassThru | Get-Disk
    "  attached second disk $($mbr.Number)"
    Check (WaitUi $proc.Id '· aperto') 'new disk opened automatically (read-only)'
    $t = UiText $proc.Id
    Check (($t -join "`n") -match "Disco $($mbr.Number) · partizione") 'opened source is the newly attached disk'
    & $shot -Out (Join-Path $work 'gui-vhd-1.png') -ProcId $proc.Id | Out-Null
    $mbrDev = Find-ExtPart $mbr.Number

    & $uia -ProcId $proc.Id -Action click -Name 'Sblocca scrittura'
    Check (WaitUi $proc.Id 'DIALOG: Sbloccare la scrittura') 'confirmation dialog shown'
    & $uia -ProcId $proc.Id -Action click -Name 'OK'
    Check (WaitUi $proc.Id 'partizione bloccata per Windows') 'GUI in read-write with the partition locked'
    & $shot -Out (Join-Path $work 'gui-vhd-2.png') -ProcId $proc.Id | Out-Null

    $out = & $cli mkdir $mbrDev 0 / must-fail 2>&1
    Check ($LASTEXITCODE -ne 0 -and "$out" -match 'cannot be locked') "second writer refused while locked ($out)"
    $out = & $cli cat $mbrDev 0 /hello.txt 2>&1
    "  read of a locked volume by another process: $out"

    & $uia -ProcId $proc.Id -Action click -Name ' Chiudi'
    Check (WaitUi $proc.Id 'scollegare il dispositivo in sicurezza') 'Chiudi flushes and releases the volume'
    & $cli mkdir $mbrDev 0 / after-close; Check ($LASTEXITCODE -eq 0) 'volume writable again after Chiudi'
}
catch {
    "EXCEPTION: $_"
    $script:fails++
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep 1
    Dismount-DiskImage -ImagePath $mbrPath -ErrorAction SilentlyContinue | Out-Null
    if (-not $GuiOnly) { Dismount-DiskImage -ImagePath $gptPath -ErrorAction SilentlyContinue | Out-Null }
    "FAILS=$script:fails"
    Stop-Transcript | Out-Null
}
