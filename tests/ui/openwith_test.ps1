<#
  GUI test for "Apri con" and write-back of edited temporary copies.
  Runs unelevated on an image (no admin needed). Leaves its screenshots in tests\work.
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $root 'tests\work'
$app = Join-Path $root 'src\WinExt4\bin\Debug\net8.0-windows\win-x64\WinExt4.exe'
$cli = Join-Path $root 'native\build\Release\e4cli.exe'
$uia = Join-Path $PSScriptRoot 'uia.ps1'
$img = Join-Path $work 'p7.img'
Copy-Item (Join-Path $root 'tests\img\ext4.img') $img -Force
$fails = 0
function Check($ok, $what) { if ($ok) { "  ok   $what" } else { "  FAIL $what"; $script:fails++ } }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function WaitUi($procId, $pattern, $seconds = 15) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $all = ((& $uia -ProcId $procId -Action text) + (& $uia -ProcId $procId -Action list)) -join "`n"
        if ($all -match $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$env:__COMPAT_LAYER = 'RunAsInvoker'
$started = Get-Date
$p = Start-Process $app -ArgumentList "`"$img`"" -PassThru
Check (WaitUi $p.Id '· aperto') 'image opened'
& $uia -ProcId $p.Id -Action click -Name 'Sblocca scrittura' | Out-Null
Check (WaitUi $p.Id 'DIALOG: Sbloccare') 'unlock confirmation'
& $uia -ProcId $p.Id -Action click -Name 'OK' | Out-Null
Check (WaitUi $p.Id 'Scrittura attiva') 'read-write'

# select hello.txt in the list
$win = $AE::RootElement.FindFirst($TS::Children, (Cond $AE::ProcessIdProperty $p.Id))
$txt = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
    (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text)), (Cond $AE::NameProperty 'hello.txt'))))
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$row = $txt
while ($row -and $row.Current.ControlType -ne [System.Windows.Automation.ControlType]::DataItem -and
       $row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $row = $walker.GetParent($row) }
$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$row.SetFocus()
Start-Sleep 1

# context menu -> "Apri con" submenu
[Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 300; $row.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait('+{F10}')
Start-Sleep 1
$menuItem = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
    (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)), (Cond $AE::NameProperty 'Apri con'))))
Check ($null -ne $menuItem) 'context menu has "Apri con"'
if ($menuItem) {
    $menuItem.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep 1
    $subs = $menuItem.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem))) |
        ForEach-Object { $_.Current.Name }
    "  handlers for .txt: $($subs -join ' | ')"
    Check ($subs -contains "Scegli un'altra app…") 'chooser entry present'
    Check ($subs.Count -ge 2) 'at least one registered program listed'
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $work 'p7-menu.png'))
}
[System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}')
Start-Sleep 1

# "Apri" -> temporary copy opened by the default program
[Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); Start-Sleep -Milliseconds 300; $row.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait('+{F10}')
Start-Sleep 1
$open = $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
    (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)), (Cond $AE::NameProperty 'Apri'))))
$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 4
$copy = Get-ChildItem (Join-Path $env:TEMP 'WinExt4') -Recurse -Filter hello.txt | Where-Object LastWriteTime -gt (Get-Date).AddYears(-1) |
    Where-Object CreationTime -gt $started | Sort-Object CreationTime | Select-Object -Last 1
Check ($null -ne $copy) "temporary copy created ($($copy.FullName))"
Check (-not $copy.IsReadOnly) 'copy is writable in read-write mode'
Get-Process Notepad -ErrorAction SilentlyContinue | Where-Object StartTime -gt $started | Stop-Process -Force

# simulate the program saving the file
Set-Content $copy.FullName "modificato da un altro programma" -NoNewline
Check (WaitUi $p.Id 'DIALOG: File modificato' 10) 'write-back question shown'
& $uia -ProcId $p.Id -Action click -Name 'Aggiorna' | Out-Null
Check (WaitUi $p.Id 'aggiornato sul disco ext' 10) 'write-back done'
$env:MSYS_NO_PATHCONV = 1
$content = & $cli cat $img 0 /hello.txt
Check ($content -eq 'modificato da un altro programma') "ext file updated ('$content')"
$ls = (& $cli ls $img 0 /) | Select-String 'hello.txt'
Check ("$ls" -match '100600\s+1000\s+1000') "owner and mode preserved ($ls)"

# closing the volume removes the temporary copies
& $uia -ProcId $p.Id -Action click -Name ' Chiudi' | Out-Null
Start-Sleep 2
Check (-not (Test-Path $copy.FullName)) 'temporary copy removed on close'
Stop-Process -Id $p.Id -Force
$fsck = wsl -d Debian -- e2fsck -fn ($img -replace '^C:', '/mnt/c' -replace '\\', '/') 2>&1
Check ($LASTEXITCODE -eq 0) 'e2fsck clean'
"FAILS=$fails"
