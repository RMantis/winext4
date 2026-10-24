<#
  Draft mode through WinExt4.dll: edits go to the delta, the image stays
  untouched until applied; export of the draft and apply are checked with
  e2fsck/debugfs in WSL. Also drafts on an image inside an archive.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$work = Join-Path $root 'tests\work'
$env:PATH = "$bin;$env:PATH"
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$none = [Threading.CancellationToken]::None
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function WslPath($p) { '/mnt/' + $p.Substring(0, 1).ToLower() + $p.Substring(2).Replace('\', '/') }
function Sha($p) { (Get-FileHash $p -Algorithm SHA256).Hash }

$img = Join-Path $work 'dl.img'
$delta = Join-Path $work 'dl.wx4draft'
Copy-Item (Join-Path $root 'tests\img\ext4.img') $img -Force
Remove-Item $delta, "$work\dl-export.img" -ErrorAction SilentlyContinue
$before = Sha $img
$src = [WinExt4.Core.SourceScanner]::ScanImage($img) | Where-Object IsExt | Select-Object -First 1
$enc = [WinExt4.Core.Ext4Volume]
$payload = [byte[]]::new(700000); (New-Object Random 3).NextBytes($payload)

"== edit as draft"
$v = [WinExt4.Core.Ext4Volume]::OpenDraft($src, $delta, [WinExt4.Core.Drafts]::Describe($src))
Check ($v.ReadWrite -and $v.DraftPath -eq $delta) "draft volume is writable"
Check ($v.GetInfo().IsDraft) "info reports draft"
$root2 = [uint32]2
$ino = $v.CreateFile($root2, $enc::Encode('bozza.bin'), 0x81a4, 0, 0)
$pin = [Runtime.InteropServices.GCHandle]::Alloc($payload, 'Pinned')
[void]$v.Write($ino, 0, $pin.AddrOfPinnedObject(), [uint32]$payload.Length)
$pin.Free()
$v.Unlink($root2, $enc::Encode('hello.txt'))
[void]$v.CreateDirectory($root2, $enc::Encode('nuova'), 0x41ed, 0, 0)
$v.Flush()
$chunks = $v.GetInfo().DraftChunks
Check ($chunks -gt 0) "changed chunks counted ($chunks)"
$v.Dispose()
Check ((Sha $img) -eq $before) "image unchanged"

"== draft info / match"
$i = [WinExt4.Core.Drafts]::Info($delta)
Check ($null -ne $i -and $i.ChangedChunks -eq $chunks -and $i.ApplyState -eq 0) "info: $($i.ChangedChunks) chunks, state $($i.ApplyState)"
Check ($i.SourceDescription -eq $img) "source description '$($i.SourceDescription)'"
Check ([WinExt4.Core.Drafts]::Matches($delta, $src)) "draft matches its source"

"== diff"
$orig = [WinExt4.Core.Ext4Volume]::Open($src, $false)
$d = [WinExt4.Core.Ext4Volume]::OpenDraft($src, $delta, '')
$changes = [WinExt4.Core.DraftDiff]::Compare($orig, $d, $null, $none)
$changes | ForEach-Object { "       $($_.Kind) $($_.Path)" }
Check (@($changes | Where-Object { $_.Kind -eq 'Added' -and $_.Path -eq '/bozza.bin' }).Count -eq 1) "added file listed"
Check (@($changes | Where-Object { $_.Kind -eq 'Removed' -and $_.Path -eq '/hello.txt' }).Count -eq 1) "removed file listed"
Check (@($changes | Where-Object { $_.Kind -eq 'Added' -and $_.Path -eq '/nuova' }).Count -eq 1) "new directory listed"

"== export the draft as an image"
$d.Dispose()
$d = [WinExt4.Core.Ext4Volume]::OpenDraft($src, $delta, '', $true)
Check (-not $d.ReadWrite) "draft view is read-only"
$t = [WinExt4.Core.Drafts]::ExportTarget($d)
$r = [WinExt4.Core.Imaging]::Export($t, "$work\dl-export.img", [WinExt4.Core.ImageFormat]::Raw, $true, $false, $null, $none)
$orig.Dispose(); $d.Dispose()
Check ($r.Raw.Bytes -eq (Get-Item $img).Length) "exported $($r.Raw.Bytes) bytes"
wsl -d Debian -- e2fsck -fn (WslPath "$work\dl-export.img") *> $null
Check ($LASTEXITCODE -eq 0) "e2fsck clean on exported image"
$got = wsl -d Debian -- sh -c "debugfs -R 'cat /bozza.bin' '$(WslPath "$work\dl-export.img")' 2>/dev/null | sha256sum"
$want = (Get-FileHash -InputStream ([IO.MemoryStream]::new($payload)) -Algorithm SHA256).Hash.ToLower()
Check ($got -like "$want*") "exported image has the new file"
Check ((Sha $img) -eq $before) "image still unchanged"

"== apply"
[WinExt4.Core.Drafts]::Apply($delta, $src, $null, $none)
Check ([WinExt4.Core.Drafts]::Info($delta).ApplyState -eq 2) "draft marked applied"
Check ((Sha $img) -eq (Sha "$work\dl-export.img")) "applied image equals the exported draft"
wsl -d Debian -- e2fsck -fn (WslPath $img) *> $null
Check ($LASTEXITCODE -eq 0) "e2fsck clean after apply"
Check (-not [WinExt4.Core.Drafts]::Matches($delta, $src)) "applied draft no longer offered"
try { [WinExt4.Core.Ext4Volume]::OpenDraft($src, $delta, '').Dispose(); Check $false "applied draft reopened" }
catch { Check $true "applied draft cannot be reopened" }

"== draft on an image inside an archive"
$arch = Get-ChildItem (Join-Path $work 'archives') -Filter '*.tar.gz' | Select-Object -First 1
$c = [WinExt4.Core.Archives.ArchiveOpener]::Open($arch.FullName, $null, $none)
$m = $c.Members | Where-Object { $_.Name -notmatch 'gpt' } | Select-Object -First 1
$as = [WinExt4.Core.SourceScanner]::ScanVirtual($m.Access, $arch.FullName, $m.Name) | Where-Object IsExt | Select-Object -First 1
$ad = Join-Path $work 'arch.wx4draft'
Remove-Item $ad -ErrorAction SilentlyContinue
$archBefore = Sha $arch.FullName
$v = [WinExt4.Core.Ext4Volume]::OpenDraft($as, $ad, [WinExt4.Core.Drafts]::Describe($as))
[void]$v.CreateDirectory($root2, $enc::Encode('da-archivio'), 0x41ed, 0, 0)
$v.Dispose()
$v = [WinExt4.Core.Ext4Volume]::OpenDraft($as, $ad, '')
$x = [uint32]0; Check ($v.TryLookup('/da-archivio', [ref]$x)) "change kept in the archive draft"
$v.Dispose()
$v = [WinExt4.Core.Ext4Volume]::OpenDraft($as, $ad, '', $true)
$t = [WinExt4.Core.Drafts]::ExportTarget($v)
$r = [WinExt4.Core.Imaging]::Export($t, "$work\arch-export.img", [WinExt4.Core.ImageFormat]::Raw, $true, $false, $null, $none)
$v.Dispose()
wsl -d Debian -- e2fsck -fn (WslPath "$work\arch-export.img") *> $null
Check ($LASTEXITCODE -eq 0) "e2fsck clean on image exported from archive draft"
Check ((Sha $arch.FullName) -eq $archBefore) "archive untouched"
Check ([WinExt4.Core.Drafts]::Matches($ad, $as)) "archive draft matches its member"
$c.Dispose()

""
if ($fails -eq 0) { "ALL DRAFT LOGIC TESTS PASSED" } else { "$fails FAILURES" }
exit $fails
