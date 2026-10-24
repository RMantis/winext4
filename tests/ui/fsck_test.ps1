<#
  fsck in the GUI: an image marked with errors shows "Controlla…", the
  check finds the errors without writing, the repair goes into a draft,
  the draft is applied and Linux e2fsck finds the result clean.
  Needs tests/work/broken.img (tests/fsck_test.sh or make_fsck_images.sh).
#>
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $root 'tests\work'
$app = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64\WinExt4.exe'
$uia = Join-Path $PSScriptRoot 'uia.ps1'
$shot = Join-Path $PSScriptRoot 'shot.ps1'
$img = Join-Path $work 'fsck-gui.img'
Copy-Item (Join-Path $work 'broken.img') $img -Force
function WslPath($p) { '/mnt/' + $p.Substring(0, 1).ToLower() + $p.Substring(2).Replace('\', '/') }
wsl -d Debian -- debugfs -w -R 'ssv state 3' (WslPath $img) *> $null   # valid + errors
$drafts = Join-Path $env:LOCALAPPDATA 'WinExt4\drafts'
Remove-Item "$drafts\TEST4-*.wx4draft" -ErrorAction SilentlyContinue
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Cond($p, $v) { New-Object System.Windows.Automation.PropertyCondition($p, $v) }
function Dialog($title) {
    for ($i = 0; $i -lt 30; $i++) {
        $d = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
            (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)), (Cond $AE::NameProperty $title))))
        if ($d) { return $d }
        Start-Sleep -Milliseconds 300
    }
}
function ById($scope, $id) { $scope.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SelectItem($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Button($scope, $name) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (Cond $AE::NameProperty $name), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)))))
}
function Main($procId) {
    for ($i = 0; $i -lt 30; $i++) {
        $w = $AE::RootElement.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $procId))
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
}
function WaitUi($procId, $pattern, $seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        if (((& $uia -ProcId $procId -Action text) -join "`n") -match $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}
function WaitStatus($dlg, $pattern, $seconds = 60) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $t = (ById $dlg 'FsckStatus').Current.Name
        if ($t -match $pattern) { return $t }
        Start-Sleep -Milliseconds 500
    }
    $t
}
$before = (Get-FileHash $img).Hash

$env:__COMPAT_LAYER = 'RunAsInvoker'
$p = Start-Process $app -ArgumentList "`"$img`"" -PassThru
Check (WaitUi $p.Id '· aperto') 'image opened'
Check (WaitUi $p.Id 'segnato con errori') 'error warning shown'
$w = Main $p.Id
$b = ById $w 'BtnFsck'
Check ($null -ne $b -and -not $b.Current.IsOffscreen) '"Controlla…" offered'

"== check only"
Invoke $b
$d = Dialog 'Controllo del filesystem'
Check ($null -ne $d) 'fsck dialog opened'
Invoke (Button $d 'Avvia')
$t = WaitStatus $d 'trovati errori|Nessun errore|non è riuscito'
Check ($t -match 'Sono stati trovati errori') "check result: $t"
$out = (ById $d 'FsckOutput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Check ($out -match "Inode 522 ref count is 5, should be 1") 'e2fsck findings shown'
Check ((Get-FileHash $img).Hash -eq $before) 'image unchanged by the check'
& $shot -Out (Join-Path $work 'p5-check.png') -ProcId $p.Id -Hwnd $d.Current.NativeWindowHandle | Out-Null

"== repair into a draft"
SelectItem (ById $d 'FsckDraft')
Invoke (Button $d 'Ripeti')
$t = WaitStatus $d 'corretti nella bozza|non è riuscito|non sono stati'
Check ($t -match 'corretti nella bozza') "repair result: $t"
Check ((Get-FileHash $img).Hash -eq $before) 'image unchanged by the draft repair'
& $shot -Out (Join-Path $work 'p5-repair.png') -ProcId $p.Id -Hwnd $d.Current.NativeWindowHandle | Out-Null
Invoke (Button $d 'Apri la bozza')
Check (WaitUi $p.Id 'Bozza: la sorgente non viene toccata') 'draft opened'
Check (WaitUi $p.Id 'lost\+found') 'listing shown'

Invoke (ById $w 'BtnDraftChanges')
$c = Dialog 'Modifiche della bozza'
Start-Sleep 1
$rows = ((ById $c 'DraftChangesList').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) |
    ForEach-Object { $_.Current.Name }) -join ' '
Check ($rows -match '/lost\+found/#519/') "recovered directory listed as added ($rows)"
Check ($rows -match 'Eliminato /dir/many_3') 'cleared entry listed as removed'
Invoke (Button $c 'Chiudi')

"== apply"
Invoke (ById $w 'BtnDraftApply')
$a = Dialog 'Applica bozza'
Invoke (Button $a 'OK')
Check (WaitUi $p.Id 'Bozza applicata' 30) 'applied'
wsl -d Debian -- e2fsck -fn (WslPath $img) *> $null
Check ($LASTEXITCODE -eq 0) 'Linux e2fsck: clean'
Check (-not (WaitUi $p.Id 'segnato con errori' 2)) 'error warning gone'
Stop-Process -Id $p.Id -Force

""
if ($fails -eq 0) { "ALL FSCK GUI TESTS PASSED" } else { "$fails FAILURES" }
exit $fails
