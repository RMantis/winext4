<#
  Preview tests: PreviewBuilder on every sample type (logic, via WinExt4.dll),
  then the GUI pane through UI Automation selection (screenshots in tests\work).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$bin = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64'
$work = Join-Path $root 'tests\work'
$srcDir = Join-Path $work 'preview-src'
$img = Join-Path $work 'preview.img'
python (Join-Path $root 'tests\make_preview_image.py') $srcDir | Out-Null
Copy-Item 'C:\Windows\System32\notepad.exe' (Join-Path $srcDir 'programma.exe')
$wsl = { param($p) $p -replace '^([A-Za-z]):', { '/mnt/' + $_.Groups[1].Value.ToLower() } -replace '\\', '/' }
wsl -d Debian -- sh -c "cp /bin/true '$(& $wsl $srcDir)/elf-bin' && mkfs.ext4 -q -F -d '$(& $wsl $srcDir)' '$(& $wsl $img)' 32M"

$env:PATH = "$bin;$env:PATH"
Add-Type -Path (Join-Path $bin 'WinExt4.dll')
Add-Type -AssemblyName PresentationCore
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }

$vol = [WinExt4.Core.Ext4Volume]::Open([WinExt4.Core.SourceScanner]::ScanImage($img)[0], $false)
$entries = $vol.ReadDir(2, $false)
function Preview($name) {
    $e = $entries | Where-Object Name -eq $name
    $item = [WinExt4.UI.EntryItem]@{ Entry = $e; Stat = $vol.Stat($e.Ino) }
    [WinExt4.UI.PreviewBuilder]::Build($vol, $item, 400, [Threading.CancellationToken]::None)
}

$p = Preview 'leggimi.txt'
Check ($p.Kind -eq 'Text' -and $p.Text.Contains('àèìòù €')) "UTF-8 text ($($p.Subtitle))"
$p = Preview 'latin1.txt'
Check ($p.Kind -eq 'Text' -and $p.Text.Contains('caffè') -and $p.Subtitle -match 'ISO-8859-1') "Latin-1 text ($($p.Subtitle))"
$p = Preview 'windows.txt'
Check ($p.Subtitle -match 'CRLF') "CRLF detected ($($p.Subtitle))"
$p = Preview 'config'
Check ($p.Kind -eq 'Text') 'extension-less text file'
$p = Preview 'grande.log'
Check ($p.Kind -eq 'Text' -and $p.Note -match 'troncato' -and $p.Text.Length -le 2097152) "big text truncated ($($p.Note))"
$p = Preview 'vuoto.txt'
Check ($p.Kind -eq 'Message' -and $p.Text -match 'vuoto') 'empty file'
$p = Preview 'foto.jpg'
Check ($p.Kind -eq 'Image' -and $p.Subtitle -match '1200×800' -and $p.Image.PixelWidth -eq 400) "JPEG decoded at panel width ($($p.Subtitle))"
$p = Preview 'logo.png'
Check ($p.Kind -eq 'Image' -and $p.Subtitle -match 'PNG') "PNG ($($p.Subtitle))"
$p = Preview 'dati.gz'
Check ($p.Kind -eq 'Hex' -and $p.Subtitle -match 'gzip') "gzip -> hex ($($p.Subtitle))"
$p = Preview 'elf-bin'
Check ($p.Kind -eq 'Hex' -and $p.Subtitle -match 'ELF') "ELF -> hex ($($p.Subtitle))"
$p = Preview 'programma.exe'
Check ($p.Kind -eq 'Hex' -and $p.Subtitle -match 'Windows') "PE -> hex ($($p.Subtitle))"
$p = Preview 'binario.bin'
Check ($p.Kind -eq 'Hex' -and $p.Text.StartsWith('00000000  00 01 02')) 'binary hex dump'
$p = Preview 'lost+found'
Check ($p.Kind -eq 'Directory') "directory ($($p.Text))"
$vol.Dispose()

# --- GUI: select files through UI Automation and screenshot the pane
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$env:__COMPAT_LAYER = 'RunAsInvoker'
$app = Start-Process (Join-Path $bin 'WinExt4.exe') -ArgumentList "`"$img`"" -PassThru
Start-Sleep 5
$win = $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)))
function Select-Row($name) {
    $t = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
    $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($t -and $t.Current.ControlType -ne [System.Windows.Automation.ControlType]::DataItem) { $t = $w.GetParent($t) }
    $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep 2
}
function Texts { $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name } }
Select-Row 'foto.jpg'
Check ((Texts) -match '1200×800') 'GUI pane shows the JPEG'
& (Join-Path $PSScriptRoot 'shot.ps1') -Out (Join-Path $work 'p8-image.png') -ProcId $app.Id | Out-Null
Select-Row 'leggimi.txt'
Check ((Texts) -match 'Testo UTF-8') 'GUI pane shows the text'
& (Join-Path $PSScriptRoot 'shot.ps1') -Out (Join-Path $work 'p8-text.png') -ProcId $app.Id | Out-Null
Stop-Process -Id $app.Id
"FAILS=$fails"
