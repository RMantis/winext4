<#
  Random access inside archives: every member read sequentially and at random
  offsets must match the original image byte for byte.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$dir = Join-Path $root 'tests\work\archives'
$env:PATH = "$bin;$env:PATH"
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$none = [Threading.CancellationToken]::None
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
$orig = @{
    gpt  = [IO.File]::ReadAllBytes((Join-Path $root 'tests\img\disk-gpt.img'))
    ext4 = [IO.File]::ReadAllBytes((Join-Path $root 'tests\img\ext4.img'))
}
$sha = [Security.Cryptography.SHA256]::Create()
$rnd = New-Object Random 1

# start from a cold index/cache so building is exercised too
Remove-Item "$env:LOCALAPPDATA\WinExt4\index\*", "$env:LOCALAPPDATA\WinExt4\cache\*" -ErrorAction SilentlyContinue

foreach ($f in Get-ChildItem $dir -File | Sort-Object Name) {
    "== $($f.Name)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $c = [WinExt4.Core.Archives.ArchiveOpener]::Open($f.FullName, $null, $none)
    "  $($c.Kind): $($c.Members.Count) image(s), opened in $($sw.ElapsedMilliseconds) ms"
    foreach ($m in $c.Members) {
        $ref = if ($m.Name -match 'gpt') { $orig.gpt } else { $orig.ext4 }
        Check ($m.Access.Length -eq $ref.Length) "$($m.Name) length $($m.Access.Length)"
        # random 4 KB reads first (cold segments), then a full sequential read
        $bad = 0
        $buf = New-Object byte[] 4096
        $sw.Restart()
        for ($i = 0; $i -lt 300; $i++) {
            $pos = [long]($rnd.NextDouble() * ($ref.Length - 4096))
            [WinExt4.Core.Archives.RandomAccessExtensions]::ReadInto($m.Access, $pos, $buf, 0, 4096)
            if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$buf, [byte[]]$ref[$pos..($pos + 4095)])) { $bad++ }
        }
        $randomMs = $sw.ElapsedMilliseconds
        Check ($bad -eq 0) "300 random 4 KB reads match ($randomMs ms)"
        $all = New-Object byte[] $m.Access.Length
        $sw.Restart()
        for ($p = 0; $p -lt $all.Length; $p += 1MB) {
            $n = [Math]::Min(1MB, $all.Length - $p)
            [WinExt4.Core.Archives.RandomAccessExtensions]::ReadInto($m.Access, $p, $all, $p, $n)
        }
        Check ([Convert]::ToHexString($sha.ComputeHash($all)) -eq [Convert]::ToHexString($sha.ComputeHash($ref))) "full content identical ($($sw.ElapsedMilliseconds) ms)"
    }
    $c.Dispose()
    # second open must reuse the saved index / cache
    $sw.Restart()
    $c = [WinExt4.Core.Archives.ArchiveOpener]::Open($f.FullName, $null, $none)
    "  reopened in $($sw.ElapsedMilliseconds) ms"
    $c.Dispose()
}
"FAILS=$fails"
