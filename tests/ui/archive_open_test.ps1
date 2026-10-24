<#
  ext filesystems inside archives: open, read, hash, refuse writes, mount via WinFsp.
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
# reference: random.bin read from the plain image through ext4core
$plain = [WinExt4.Core.SourceScanner]::ScanImage((Join-Path $root 'tests\img\ext4.img'))[0]
$pv = [WinExt4.Core.Ext4Volume]::Open($plain, $false)
function FileHash($vol, $path) {
    $ino = $vol.Lookup($path); $st = $vol.Stat($ino)
    $buf = New-Object byte[] $st.Size
    $gh = [Runtime.InteropServices.GCHandle]::Alloc($buf, 'Pinned')
    try { $n = $vol.Read($ino, 0, $gh.AddrOfPinnedObject(), [uint32]$st.Size) } finally { $gh.Free() }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buf))
}
$refHash = FileHash $pv '/dir/random.bin'
$pv.Dispose()

foreach ($f in Get-ChildItem $dir -File | Sort-Object Name) {
    "== $($f.Name)"
    $c = [WinExt4.Core.Archives.ArchiveOpener]::Open($f.FullName, $null, $none)
    $sources = foreach ($m in $c.Members) { [WinExt4.Core.SourceScanner]::ScanVirtual($m.Access, $f.FullName, $m.Name) }
    $ext = @($sources | Where-Object IsExt)
    Check ($ext.Count -ge 1) "$($ext.Count) ext filesystem(s): $(($ext | ForEach-Object { "$($_.ArchiveMember)#$($_.PartitionNumber)" }) -join ', ')"
    foreach ($s in $ext) {
        $v = [WinExt4.Core.Ext4Volume]::Open($s, $false)
        $names = $v.ReadDir(2, $false) | ForEach-Object Name
        Check ($names -contains 'hello.txt' -and $names -contains 'dir') "  root listing ($($names.Count) entries)"
        Check ((FileHash $v '/dir/random.bin') -eq $refHash) '  random.bin identical'
        $v.Dispose()
        try { [WinExt4.Core.Ext4Volume]::Open($s, $true) | Out-Null; Check $false '  write refused' }
        catch { Check ($_.Exception.InnerException.Code -eq 30) '  write refused (EROFS)' }
    }
    $c.Dispose()
}

"== hash of a partition inside a .tar.xz"
$c = [WinExt4.Core.Archives.ArchiveOpener]::Open((Join-Path $dir 'immagini.tar.xz'), $null, $none)
$m = $c.Members | Where-Object Name -match 'gpt'
$s = [WinExt4.Core.SourceScanner]::ScanVirtual($m.Access, 'immagini.tar.xz', $m.Name) | Where-Object IsExt
$h = [WinExt4.Core.Imaging]::Hash([WinExt4.Core.RawTarget]::Partition($s), $null, $none)
$ref = [WinExt4.Core.Imaging]::Hash([WinExt4.Core.RawTarget]::Partition(([WinExt4.Core.SourceScanner]::ScanImage((Join-Path $root 'tests\img\disk-gpt.img')) | Where-Object IsExt)), $null, $none)
Check ($h.Sha256 -eq $ref.Sha256) "partition hash inside archive = partition of the original disk image"
$c.Dispose()

"== mount an image inside a .tar.gz with WinFsp (read-only)"
$c = [WinExt4.Core.Archives.ArchiveOpener]::Open((Join-Path $dir 'immagini.tar.gz'), $null, $none)
$m = $c.Members | Where-Object Name -match 'longname'
$s = [WinExt4.Core.SourceScanner]::ScanVirtual($m.Access, 'immagini.tar.gz', $m.Name) | Where-Object IsExt
$v = [WinExt4.Core.Ext4Volume]::Open($s, $false)
$mount = [WinExt4.Mount.MountSession]::Mount($v, (New-Object WinExt4.Core.AppSettings), $null)
try {
    $root2 = $mount.DriveLetter + '\'
    Check ((Get-Content ($root2 + 'hello.txt') -Raw) -eq "hello from ext4`n") "read hello.txt from $root2"
    Check ((Get-FileHash ($root2 + 'dir\random.bin') -Algorithm SHA256).Hash -eq $refHash) 'random.bin via Windows identical'
    try { Set-Content ($root2 + 'x.txt') 'x'; Check $false 'write refused on the drive' } catch { Check $true 'write refused on the drive' }
}
finally {
    $mount.Dispose()
    $v.Dispose()
    $c.Dispose()
}
"FAILS=$fails"
