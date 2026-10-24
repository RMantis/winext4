<#
  Tests the logic behind "Apri con" without driving the mouse: handler
  enumeration, temporary copies, save detection and write-back.
  Loads WinExt4.dll directly; runs unelevated on an image.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$work = Join-Path $root 'tests\work'
$img = Join-Path $work 'p7.img'
Copy-Item (Join-Path $root 'tests\img\ext4.img') $img -Force
$env:PATH = "$bin;$env:PATH"   # ext4core.dll
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }

$handlers = [WinExt4.UI.ShellOpen]::Handlers('.txt')
"  .txt handlers: $(($handlers | ForEach-Object Name) -join ' | ')"
Check ($handlers.Count -ge 1) 'registered programs found for .txt'
Check (($handlers | Where-Object { $_.Icon }).Count -ge 1) 'handler icons loaded'
$h2 = [WinExt4.UI.ShellOpen]::Handlers('.png')
"  .png handlers: $(($h2 | ForEach-Object Name) -join ' | ')"

$src = [WinExt4.Core.SourceScanner]::ScanImage($img)[0]
$vol = [WinExt4.Core.Ext4Volume]::Open($src, $true)
$temp = New-Object WinExt4.Core.TempCopies
# The Modified event fires from a FileSystemWatcher; here we check the state it relies on.
$ino = $vol.Lookup('/hello.txt')
$copy = $temp.Extract($vol, $ino, '/hello.txt', 'hello.txt', [Threading.CancellationToken]::None)
Check (Test-Path $copy.LocalPath) "copy extracted to $($copy.LocalPath)"
Check ((Get-Content $copy.LocalPath -Raw) -eq "hello from ext4`n") 'copy content'
Check (-not (Get-Item $copy.LocalPath).IsReadOnly) 'copy writable (volume read-write)'
Check (-not $copy.LocallyModified) 'not modified right after extraction'

Start-Sleep -Milliseconds 1200
Set-Content $copy.LocalPath 'modificato da un altro programma' -NoNewline
Check ($copy.LocallyModified) 'save detected'
[WinExt4.Core.TempCopies]::WriteBack($vol, $copy)
Check (-not $copy.LocallyModified) 'synced after write-back'
$st = $vol.Stat($ino)
Check ($st.Size -eq 32) "ext file size $($st.Size)"
Check ($st.Uid -eq 1000 -and $st.Gid -eq 1000 -and $st.Permissions -eq 384) 'owner and mode (600) preserved'
$buf = New-Object byte[] 64
$gh = [Runtime.InteropServices.GCHandle]::Alloc($buf, "Pinned"); $n = $vol.Read($ino, 0, $gh.AddrOfPinnedObject(), 64); $gh.Free()
Check ([Text.Encoding]::UTF8.GetString($buf, 0, $n) -eq 'modificato da un altro programma') 'ext content updated'

# read-only volume: copies are read-only
$vol.Dispose()
$vol = [WinExt4.Core.Ext4Volume]::Open($src, $false)
$ro = $temp.Extract($vol, $ino, '/hello.txt', 'hello.txt', [Threading.CancellationToken]::None)
Check ((Get-Item $ro.LocalPath).IsReadOnly) 'copy read-only when volume is read-only'
$dir = Split-Path (Split-Path $ro.LocalPath)
$temp.Clear()
Check (-not (Test-Path $dir)) 'Clear() removes the temporary folder'
$vol.Dispose()

$fsck = wsl -d Debian -- e2fsck -fn ($img -replace '^C:', '/mnt/c' -replace '\\', '/') 2>&1
Check ($LASTEXITCODE -eq 0) 'e2fsck clean'
"FAILS=$fails"
