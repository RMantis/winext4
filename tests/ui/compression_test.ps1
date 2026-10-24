<#
  Zero-fill, compressed/VHD export and writing from compressed images,
  verified with independent tools (gzip/xz in WSL, python zstandard, 7-Zip for VHD).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$work = Join-Path $root 'tests\work'
$cli = Join-Path $root 'native\build\Release\e4cli.exe'
$7z = "$env:USERPROFILE\scoop\apps\7zip\current\7z.exe"
$env:PATH = "$bin;$env:PATH"
$env:MSYS_NO_PATHCONV = 1
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$none = [Threading.CancellationToken]::None
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function W($p) { $p -replace '^([A-Za-z]):', { '/mnt/' + $_.Groups[1].Value.ToLower() } -replace '\\', '/' }

"== zero free space"
$disk = Join-Path $work 'cmp-gpt.img'
Copy-Item (Join-Path $root 'tests\img\disk-gpt.img') $disk -Force
$src = [WinExt4.Core.SourceScanner]::ScanImage($disk) | Where-Object IsExt
$junk = Join-Path $work 'junk.bin'
[IO.File]::WriteAllBytes($junk, [byte[]](1..8000000 | ForEach-Object { 0 }) )   # placeholder, overwritten below
$rnd = New-Object byte[] 8000000; (New-Object Random 7).NextBytes($rnd); [IO.File]::WriteAllBytes($junk, $rnd)
$vol = [WinExt4.Core.Ext4Volume]::Open($src, $true)
$list = [System.Collections.Generic.List[string]]::new(); $list.Add($junk)
$t = New-Object WinExt4.Core.FileTransfer($vol, (New-Object WinExt4.Core.AppSettings), $null, $none)
$t.Import($list, 2)
$vol.Unlink(2, [Text.Encoding]::UTF8.GetBytes('junk.bin'))
$z = $vol.ZeroFreeSpace($none, $null)
Check ($z.Item2 -ge 7800) "garbage blocks zeroed ($($z.Item2) of $($z.Item1) free)"
$z2 = $vol.ZeroFreeSpace($none, $null)
Check ($z2.Item2 -eq 0) 'second pass writes nothing'
$vol.Dispose()
wsl -d Debian -- bash -c "dd if='$(W $disk)' bs=1M skip=1 count=64 status=none of=/tmp/zf.img; e2fsck -fn /tmp/zf.img >/dev/null 2>&1"
Check ($LASTEXITCODE -eq 0) 'e2fsck clean after zero-fill'

$part = [WinExt4.Core.RawTarget]::Partition($src)
$rawHash = [WinExt4.Core.Imaging]::Hash($part, $null, $none).Sha256
"== export formats"
$exports = @{}
foreach ($fmt in 'Raw', 'Gzip', 'Zstd', 'Xz', 'Vhd') {
    $f = [WinExt4.Core.ImageFormat]$fmt
    $out = Join-Path $work ("cmp-$fmt" + [WinExt4.Core.ImageFormats]::Extension($f))
    Remove-Item "$out*" -ErrorAction SilentlyContinue
    $r = [WinExt4.Core.Imaging]::Export($part, $out, $f, $true, $true, $null, $none)
    $exports[$fmt] = $out
    $size = (Get-Item $out).Length
    "  $fmt : $([WinExt4.Core.Format]::Size([uint64]$size)) for 64 MB"
    Check ($r.Raw.Sha256 -eq $rawHash) "$fmt raw hash = partition hash"
    $independent = switch ($fmt) {
        'Raw'  { (Get-FileHash $out -Algorithm SHA256).Hash.ToLower() }
        'Gzip' { (wsl -d Debian -- bash -c "gzip -dc '$(W $out)' | sha256sum").Split(' ')[0] }
        'Xz'   { (wsl -d Debian -- bash -c "xz -dc '$(W $out)' | sha256sum").Split(' ')[0] }
        'Zstd' { python -c "import zstandard,hashlib,sys; d=zstandard.ZstdDecompressor().stream_reader(open(sys.argv[1],'rb')); h=hashlib.sha256(); [h.update(c) for c in iter(lambda: d.read(1<<20), b'')]; print(h.hexdigest())" $out }
        'Vhd'  { $tmp = Join-Path $work 'vhd-extract'; Remove-Item $tmp -Recurse -ErrorAction SilentlyContinue; & $7z e -tvhd $out "-o$tmp" -y | Out-Null; (Get-FileHash (Get-ChildItem $tmp | Select-Object -First 1).FullName -Algorithm SHA256).Hash.ToLower() }
    }
    Check ($independent -eq $rawHash) "$fmt decompressed independently = partition"
    if ($fmt -ne 'Raw') {
        $c = wsl -d Debian -- bash -c "cd '$(W $work)' && sha256sum -c '$(Split-Path $out -Leaf).sha256'"
        Check ("$c" -match ': OK') "$fmt file sidecar: $c"
        $rawSide = [WinExt4.Core.Sidecar]::RawName($out) + '.sha256'
        Check ((Test-Path $rawSide) -and ((Get-Content $rawSide) -match $rawHash)) "$fmt content sidecar $(Split-Path $rawSide -Leaf)"
    }
}

"== write from compressed images"
foreach ($fmt in 'Gzip', 'Zstd', 'Xz', 'Vhd') {
    $img = Join-Path $work ("cmp-$fmt" + [WinExt4.Core.ImageFormats]::Extension([WinExt4.Core.ImageFormat]$fmt))
    $target = Join-Path $work 'cmp-target.img'
    Copy-Item $disk $target -Force
    wsl -d Debian -- sh -c "dd if=/dev/zero of='$(W $target)' bs=1M seek=1 count=64 conv=notrunc status=none"
    $ts = [WinExt4.Core.SourceScanner]::ScanImage($target) | Where-Object PartitionNumber -eq 1
    $exp = [WinExt4.Core.Sidecar]::Find($img)
    $r = [WinExt4.Core.Imaging]::WriteImage($img, [WinExt4.Core.RawTarget]::Partition($ts), $exp, $true, $true, $null, $none)
    Check ($r.Readback.Sha256 -eq $rawHash -and $exp) "$fmt written, read-back = partition (sidecar $($exp.File))"
    Check ((Get-FileHash $target).Hash -eq (Get-FileHash $disk).Hash) "$fmt target identical to source disk"
}

"== corrupted compressed image is refused"
$bad = Join-Path $work 'bad.img.zst'
Copy-Item (Join-Path $work 'cmp-Zstd.img.zst') $bad -Force
Remove-Item "$workad.img.zst.*", "$workad.img.*" -ErrorAction SilentlyContinue
$bytes = [IO.File]::ReadAllBytes($bad); $bytes[$bytes.Length / 2] = $bytes[$bytes.Length / 2] -bxor 0xFF; [IO.File]::WriteAllBytes($bad, $bytes)
$before = (Get-FileHash $target).Hash
try { [WinExt4.Core.Imaging]::WriteImage($bad, [WinExt4.Core.RawTarget]::Partition($ts), [WinExt4.Core.Sidecar]::Find($bad), $true, $true, $null, $none) | Out-Null; Check $false 'corrupted image refused' }
catch { Check $true "corrupted image refused: $($_.Exception.InnerException.Message)" }
Check ((Get-FileHash $target).Hash -eq $before) 'nothing written'
"FAILS=$fails"
