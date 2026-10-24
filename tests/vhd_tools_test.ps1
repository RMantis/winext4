<#
  Real-disk test (elevated) for partition tools, hashing/imaging and
  superblock editing, on an attached VHD. Log: tests\work\vhd-tools.log
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot
$work = Join-Path $root 'tests\work'
Start-Transcript (Join-Path $work 'vhd-tools.log') -Force | Out-Null
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$cli = Join-Path $root 'native\build\Release\e4cli.exe'
$env:PATH = "$bin;$env:PATH"
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$none = [Threading.CancellationToken]::None
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function DiskSource($n) { [WinExt4.Core.SourceScanner]::ScanDisks() | Where-Object { $_.DiskNumber -eq $n -and $_.PartitionNumber -eq 1 } }

$vhd = Join-Path $work 'vhd-gpt.vhd'
try {
    $disk = Mount-DiskImage -ImagePath $vhd -PassThru | Get-Disk
    $n = $disk.Number
    Start-Sleep 3
    "== partition table on a real disk (disk $n)"
    $s = DiskSource $n
    Check ($s.IsExt -and $s.PartitionStyle -eq 'Gpt') "GPT ext4 partition found ($($s.Device))"
    Check ($s.GptType -eq '0FC63DAF-8483-4772-8E79-3D69D8477DE4' -and $s.GptId -eq '2458D8DB-BD02-354B-9851-421ED09778F5') "type and PARTUUID ($($s.GptId))"
    Check ($s.GptStatus -eq ([WinExt4.Core.GptStatus]'Checked,PrimaryOk,BackupOk,CopiesMatch')) "GPT copies verified ($($s.GptStatus))"
    Check (-not [WinExt4.Core.Imaging]::SystemDisks().Contains($n)) 'VHD is not a system disk'
    Check ([WinExt4.Core.Imaging]::SystemDisks().Count -ge 1) "system disk detected ($([WinExt4.Core.Imaging]::SystemDisks() -join ','))"
    [WinExt4.Core.PartitionTypes]::Edit($s, $null, $null, 'vhd-test', [uint64]9223372036854775808)
    Start-Sleep 2
    $s = DiskSource $n
    Check ($s.GptName -eq 'vhd-test' -and $s.GptAttributes -eq [uint64]9223372036854775808) "name/attributes changed via IOCTL ($($s.GptName), 0x$('{0:X}' -f $s.GptAttributes))"
    Check ($s.GptStatus -band 4) 'GPT copies still consistent'

    "== superblock edit on the raw partition"
    & $cli tune $s.Device 0 1 VHD-LABEL; Check ($LASTEXITCODE -eq 0) 'label set on \\.\HarddiskNPartitionM'
    $s = DiskSource $n
    Check ($s.Label -eq 'VHD-LABEL') "label read back from disk scan ($($s.Label))"

    "== hash + export of the partition"
    $part = [WinExt4.Core.RawTarget]::Partition($s)
    $h = [WinExt4.Core.Imaging]::Hash($part, $null, $none)
    $out = Join-Path $work 'vhd-p1.img'
    Remove-Item "$out*" -ErrorAction SilentlyContinue
    $e = [WinExt4.Core.Imaging]::Export($part, $out, $true, $true, $null, $none)
    Check ($e.Sha256 -eq $h.Sha256 -and (Test-Path "$out.sha256")) "export of the disk partition (sha256 $($h.Sha256.Substring(0,16))…)"
    $whole = [WinExt4.Core.RawTarget]::WholeDisk($s)
    $hw = [WinExt4.Core.Imaging]::Hash($whole, $null, $none)
    Check ($hw.Bytes -eq $disk.Size) "whole-disk hash covers $($hw.Bytes) bytes"

    "== write an MBR disk image over the whole disk (all volumes locked)"
    $src = Join-Path $work 'vhd-write-src.img'
    Copy-Item (Join-Path $root 'tests\img\disk-mbr.img') $src -Force
    [WinExt4.Core.Sidecar]::Write($src, [WinExt4.Core.Imaging]::HashFile($src, $null, $none))
    $exp = [WinExt4.Core.Sidecar]::Find($src)
    $r = [WinExt4.Core.Imaging]::WriteImage($src, $whole, $exp, $true, $true, $null, $none)
    Check ($r.Readback.Sha256 -eq $r.Image.Sha256) 'whole-disk write verified by read-back'
    Start-Sleep 3
    $after = [WinExt4.Core.SourceScanner]::ScanDisks() | Where-Object { $_.DiskNumber -eq $n }
    Check (($after | Where-Object { $_.PartitionStyle -eq 'Mbr' -and $_.IsExt }).Count -eq 1) "disk now has the MBR layout ($(($after | ForEach-Object { "p$($_.PartitionNumber) $($_.PartitionTypeText)" }) -join ', '))"

    "== a locked target is refused"
    $p2 = $after | Where-Object IsExt
    $hold = [WinExt4.Core.Ext4Volume]::Open($p2, $true)
    try { [WinExt4.Core.Imaging]::WriteImage($src, [WinExt4.Core.RawTarget]::WholeDisk($p2), $null, $false, $false, $null, $none) | Out-Null; Check $false 'write while volume locked refused' }
    catch { Check ($_.Exception.InnerException.Message -match 'in uso|locked') "write while another handle holds the volume refused: $($_.Exception.InnerException.Message)" }
    $hold.Dispose()
}
catch {
    "EXCEPTION: $_"
    $fails++
}
finally {
    Dismount-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue | Out-Null
    "FAILS=$fails"
    Stop-Transcript | Out-Null
}
