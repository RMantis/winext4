<#
  Partition properties dialog: file count, filesystem edits (label, reserved
  space) and partition edits (GPT name, no-automount), checked with WSL tools.
#>
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $root 'tests\work'
$app = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64\WinExt4.exe'
$uia = Join-Path $PSScriptRoot 'uia.ps1'
$shot = Join-Path $PSScriptRoot 'shot.ps1'
$img = Join-Path $work 'props-gpt.img'
Copy-Item (Join-Path $root 'tests\img\disk-gpt.img') $img -Force
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
function Find($scope, $name, $type) {
    $c = Cond $AE::NameProperty $name
    if ($type) { $c = New-Object System.Windows.Automation.AndCondition($c, (Cond $AE::ControlTypeProperty $type)) }
    $scope.FindFirst($TS::Descendants, $c)
}
function ById($scope, $id) { $scope.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText($el, $t) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($t) }
function Tab($dlg, $name) { (Find $dlg $name ([System.Windows.Automation.ControlType]::TabItem)).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1 }
function ConfirmOk() { Start-Sleep 1; $d = $AE::RootElement.FindFirst($TS::Descendants, (Cond $AE::NameProperty 'OK')); if ($d) { Invoke $d } }
function WaitUi($procId, $pattern, $seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        if (((& $uia -ProcId $procId -Action text) -join "`n") -match $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}

$env:__COMPAT_LAYER = 'RunAsInvoker'
$p = Start-Process $app -ArgumentList "`"$img`"" -PassThru
Check (WaitUi $p.Id '· aperto') 'image opened'
& $uia -ProcId $p.Id -Action click -Name 'Sblocca scrittura' | Out-Null
Start-Sleep 1; & $uia -ProcId $p.Id -Action click -Name 'OK' | Out-Null
Check (WaitUi $p.Id 'Scrittura attiva') 'read-write'

& $uia -ProcId $p.Id -Action click -Name 'Proprietà' | Out-Null
$dlg = Dialog 'Proprietà — TEST4'
Check ($null -ne $dlg) 'properties dialog opened'
Invoke (Find $dlg 'Conta file' ([System.Windows.Automation.ControlType]::Button))
Start-Sleep 3
$count = (ById $dlg 'CountResult').Current.Name
"  count: $count"
Check ($count -match '^507 file') 'file count = 507'
& $shot -Out (Join-Path $work 'p9-general.png') -ProcId $p.Id -Hwnd $dlg.Current.NativeWindowHandle | Out-Null

Tab $dlg 'Filesystem'
SetText (ById $dlg 'FsLabel') 'PROPS-TEST'
SetText (ById $dlg 'FsReserved') '1'
& $shot -Out (Join-Path $work 'p9-fs.png') -ProcId $p.Id -Hwnd $dlg.Current.NativeWindowHandle | Out-Null
Invoke (Find $dlg 'Applica al filesystem' ([System.Windows.Automation.ControlType]::Button))
ConfirmOk
Start-Sleep 2

Tab $dlg 'Partizione'
SetText (ById $dlg 'PartName') 'dati-linux'
$na = Find $dlg 'Nessuna lettera di unità in Windows (no automount)'
$na.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
& $shot -Out (Join-Path $work 'p9-part.png') -ProcId $p.Id -Hwnd $dlg.Current.NativeWindowHandle | Out-Null
Invoke (Find $dlg 'Applica alla partizione' ([System.Windows.Automation.ControlType]::Button))
ConfirmOk
Check (WaitUi $p.Id 'Tabella delle partizioni aggiornata|· scrittura' 20) 'partition edited and volume reopened'
Start-Sleep 2
Check (WaitUi $p.Id 'PROPS-TEST') 'new label shown in the main window'
Stop-Process -Id $p.Id -Force
Start-Sleep 1

$w = $img -replace '^C:', '/mnt/c' -replace '\\', '/'
$d = wsl -d Debian -- sfdisk -d $w
Check ("$d" -match 'name="dati-linux"' -and "$d" -match 'GUID:63') "sfdisk: $($d | Select-Object -Last 1)"
$h = wsl -d Debian -- bash -c "dd if=$w bs=1M skip=1 count=64 status=none of=/tmp/pp.img; dumpe2fs -h /tmp/pp.img 2>/dev/null | grep -E 'volume name|Reserved block count|Block count'; e2fsck -fn /tmp/pp.img >/dev/null 2>&1; echo fsck=`$?"
$h
Check ("$h" -match 'PROPS-TEST') 'dumpe2fs: label'
Check ("$h" -match 'Reserved block count:\s+655\b') 'dumpe2fs: 1% reserved (655 blocks)'
Check ("$h" -match 'fsck=0') 'e2fsck clean'
"FAILS=$fails"
