<#
  Hash / export / write-image / file-hash logic on image files (no admin),
  cross-checked with Linux tools in WSL.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$work = Join-Path $root 'tests\work'
$env:PATH = "$bin;$env:PATH"
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function W($p) { $p -replace '^([A-Za-z]):', { '/mnt/' + $_.Groups[1].Value.ToLower() } -replace '\\', '/' }
$none = [Threading.CancellationToken]::None

$disk = Join-Path $work 'imaging-gpt.img'
Copy-Item (Join-Path $root 'tests\img\disk-gpt.img') $disk -Force
$src = [WinExt4.Core.SourceScanner]::ScanImage($disk) | Where-Object IsExt
$part = [WinExt4.Core.RawTarget]::Partition($src)
$whole = [WinExt4.Core.RawTarget]::WholeDisk($src)

"== hash"
$h = [WinExt4.Core.Imaging]::Hash($part, $null, $none)
$lin = wsl -d Debian -- bash -c "dd if='$(W $disk)' bs=1M skip=1 count=64 status=none | sha256sum; dd if='$(W $disk)' bs=1M skip=1 count=64 status=none | md5sum"
Check ($lin[0] -match $h.Sha256) "partition SHA-256 = Linux ($($h.Sha256.Substring(0,16))…)"
Check ($lin[1] -match $h.Md5) "partition MD5 = Linux"
$hw = [WinExt4.Core.Imaging]::Hash($whole, $null, $none)
Check ($hw.Sha256 -eq (Get-FileHash $disk -Algorithm SHA256).Hash.ToLower()) 'whole image SHA-256 = Get-FileHash'
Check ($hw.Bytes -eq (Get-Item $disk).Length) 'whole image size'

"== export"
$out = Join-Path $work 'exported-p1.img'
Remove-Item "$out*" -ErrorAction SilentlyContinue
$e = [WinExt4.Core.Imaging]::Export($part, $out, [WinExt4.Core.ImageFormat]::Raw, $true, $true, $null, $none).Raw
Check ($e.Sha256 -eq $h.Sha256) 'export hash equals partition hash'
Check ((Test-Path "$out.md5") -and (Test-Path "$out.sha256")) 'sidecar files written'
$c = wsl -d Debian -- sh -c "cd '$(W $work)' && md5sum -c exported-p1.img.md5 && sha256sum -c exported-p1.img.sha256"
Check (($c -join ' ') -match 'exported-p1.img: OK.*exported-p1.img: OK') "md5sum -c / sha256sum -c: $($c -join ' | ')"
$sparse = (fsutil sparse queryflag $out) -join ' '
Check ($sparse -match 'impostato|is set|set') "output is sparse ($sparse)"
$fsck = wsl -d Debian -- e2fsck -fn (W $out) 2>&1
Check ($LASTEXITCODE -eq 0) 'exported partition passes e2fsck'

"== write image into a partition"
$target = Join-Path $work 'imaging-target.img'
Copy-Item $disk $target -Force
wsl -d Debian -- sh -c "dd if=/dev/zero of='$(W $target)' bs=1M seek=1 count=64 conv=notrunc status=none"
$tsrc = [WinExt4.Core.SourceScanner]::ScanImage($target) | Where-Object PartitionNumber -eq 1
Check (-not $tsrc.IsExt) 'target partition zeroed'
$ttarget = [WinExt4.Core.RawTarget]::Partition($tsrc)
$expected = [WinExt4.Core.Sidecar]::Find($out)
Check ($expected.Algorithm -eq 'sha256') "sidecar found ($($expected.File))"
$r = [WinExt4.Core.Imaging]::WriteImage($out, $ttarget, $expected, $true, $true, $null, $none)
Check ($r.Readback.Sha256 -eq $h.Sha256) 'read-back matches'
$tsrc2 = [WinExt4.Core.SourceScanner]::ScanImage($target) | Where-Object PartitionNumber -eq 1
Check ($tsrc2.IsExt -and $tsrc2.Label -eq 'TEST4') 'ext4 is back in the target partition'
Check ((Get-FileHash $target).Hash -eq (Get-FileHash $disk).Hash) 'target image identical to the original disk image'

"== wrong sidecar is refused before writing"
Set-Content "$out.sha256" ("0" * 64 + " *exported-p1.img")
$bad = [WinExt4.Core.Sidecar]::Find($out)
wsl -d Debian -- sh -c "dd if=/dev/zero of='$(W $target)' bs=1M seek=1 count=1 conv=notrunc status=none"
$before = (Get-FileHash $target).Hash
try { [WinExt4.Core.Imaging]::WriteImage($out, $ttarget, $bad, $true, $true, $null, $none) | Out-Null; Check $false 'mismatch refused' }
catch { Check ($_.Exception.InnerException.Message -match 'non corrisponde') "mismatch refused: $($_.Exception.InnerException.Message)" }
Check ((Get-FileHash $target).Hash -eq $before) 'nothing written after a failed pre-verification'

"== file hashes inside the filesystem"
$vol = [WinExt4.Core.Ext4Volume]::Open($src, $false)
$dirIno = $vol.Lookup('/dir')
$entries = [System.Collections.Generic.List[WinExt4.Core.Ext4DirEntry]]::new(); $vol.ReadDir($dirIno, $false) | Where-Object { $_.Name -in 'random.bin', 'città.txt', 'sub' } | ForEach-Object { $entries.Add($_) }
$list = [WinExt4.Core.FileHashes]::Compute($vol, $dirIno, $entries, [Security.Cryptography.HashAlgorithmName]::MD5, $null, $none)
$text = [WinExt4.Core.FileHashes]::Format($list)
$text
$lin = wsl -d Debian -- sh -c "debugfs -R 'cat /dir/random.bin' '$(W $disk)' 2>/dev/null | md5sum" 2>$null
# disk-gpt.img is a disk image: debugfs needs the partition, so compare via the exported partition
$lin = wsl -d Debian -- sh -c "debugfs -R 'cat /dir/random.bin' '$(W $out)' 2>/dev/null | md5sum"
Check ($text -match ($lin -split ' ')[0]) 'random.bin MD5 = Linux'
Check ($text -match 'sub/inner.txt') 'recursion into sub-directories'
$ok = [WinExt4.Core.FileHashes]::Verify($vol, '/dir', $text, $null, $none)
Check (($ok | Where-Object Status -ne 'OK').Count -eq 0) "verify: $(($ok | ForEach-Object Status) -join ',')"
$tampered = $text -replace '^[0-9a-f]{32}', ('f' * 32)
$tampered += "0123456789abcdef0123456789abcdef  manca.txt`n"
$res = [WinExt4.Core.FileHashes]::Verify($vol, '/dir', $tampered, $null, $none)
Check (($res | Where-Object Status -eq 'DIVERSO').Count -eq 1 -and ($res | Where-Object Status -eq 'MANCANTE').Count -eq 1) "tampered list: $(($res | ForEach-Object { $_.Status + ' ' + $_.RelativePath }) -join '; ')"
$vol.Dispose()
"FAILS=$fails"
