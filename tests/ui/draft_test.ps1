<#
  Draft mode in the GUI: start a draft, create a folder, list the changes,
  close the app, reopen the .wx4draft file, apply it. The image must stay
  unchanged until applied; checked with debugfs/e2fsck in WSL.
#>
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $root 'tests\work'
$app = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64\WinExt4.exe'
$uia = Join-Path $PSScriptRoot 'uia.ps1'
$shot = Join-Path $PSScriptRoot 'shot.ps1'
$img = Join-Path $work 'draft-gui.img'
Copy-Item (Join-Path $root 'tests\img\ext4.img') $img -Force
$drafts = Join-Path $env:LOCALAPPDATA 'WinExt4\drafts'
Remove-Item "$drafts\TEST4-*.wx4draft" -ErrorAction SilentlyContinue
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }
function WslPath($p) { '/mnt/' + $p.Substring(0, 1).ToLower() + $p.Substring(2).Replace('\', '/') }
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
function Button($scope, $name) {
    $scope.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (Cond $AE::NameProperty $name), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)))))
}
# answers the MessageBox titled $title with the button $answer
function Answer($title, $answer) {
    $d = Dialog $title
    if (-not $d) { return $false }
    $b = Button $d $answer
    if (-not $b) { return $false }
    Invoke $b; $true
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
$before = (Get-FileHash $img).Hash

$env:__COMPAT_LAYER = 'RunAsInvoker'
"== start a draft"
$p = Start-Process $app -ArgumentList "`"$img`"" -PassThru
Check (WaitUi $p.Id '· aperto') 'image opened'
$w = Main $p.Id
Invoke (ById $w 'BtnDraft')
Check (Answer 'Modifica come bozza' 'OK') 'draft explanation confirmed'
Check (WaitUi $p.Id 'Bozza: la sorgente non viene toccata') 'draft banner shown'
Check (WaitUi $p.Id '· bozza') 'source marked as draft'
$delta = Get-ChildItem $drafts -Filter 'TEST4-*.wx4draft' | Select-Object -First 1
Check ($null -ne $delta) "draft file created ($($delta.Name))"

"== edit"
& $uia -ProcId $p.Id -Action click -Name 'Nuova cartella' | Out-Null
$d = Dialog 'Nuova cartella'
$box = $d.FindFirst($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('cartella-da-bozza')
Invoke (Button $d 'OK')
Check (WaitUi $p.Id 'cartella-da-bozza') 'folder visible in the draft'
Check ((Get-FileHash $img).Hash -eq $before) 'image unchanged'
& $shot -Out (Join-Path $work 'p6-draft.png') -ProcId $p.Id | Out-Null

"== changes"
Invoke (ById $w 'BtnDraftChanges')
$d = Dialog 'Modifiche della bozza'
Check ($null -ne $d) 'changes dialog opened'
Start-Sleep 1
$rows = ((ById $d 'DraftChangesList').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) |
    ForEach-Object { $_.Current.Name }) -join ' '
Check ($rows -match 'Aggiunto /cartella-da-bozza/') "change listed ($rows)"
& $shot -Out (Join-Path $work 'p6-changes.png') -ProcId $p.Id -Hwnd $d.Current.NativeWindowHandle | Out-Null
Invoke (Button $d 'Chiudi')

"== close and resume from the .wx4draft file"
Stop-Process -Id $p.Id -Force   # an abrupt end: the draft must survive it
Start-Sleep 1
$p = Start-Process $app -ArgumentList "`"$($delta.FullName)`"" -PassThru
Check (WaitUi $p.Id 'Bozza: la sorgente non viene toccata' 25) 'draft reopened from its file (image found by path)'
Check (WaitUi $p.Id 'cartella-da-bozza') 'change still there'
Check ((Get-FileHash $img).Hash -eq $before) 'image still unchanged'

"== apply"
$w = Main $p.Id
Invoke (ById $w 'BtnDraftApply')
Check (Answer 'Applica bozza' 'OK') 'apply confirmed'
Check (WaitUi $p.Id 'Bozza applicata' 30) 'applied'
Check (WaitUi $p.Id '· aperto') 'source reopened read-only'
Check (-not (Test-Path $delta.FullName)) 'draft file removed after apply'
$ls = wsl -d Debian -- debugfs -R 'ls /' (WslPath $img) 2>$null
Check (($ls -join ' ') -match 'cartella-da-bozza') 'folder in the image (debugfs)'
wsl -d Debian -- e2fsck -fn (WslPath $img) *> $null
Check ($LASTEXITCODE -eq 0) 'e2fsck clean'
Stop-Process -Id $p.Id -Force

""
if ($fails -eq 0) { "ALL DRAFT GUI TESTS PASSED" } else { "$fails FAILURES" }
exit $fails
