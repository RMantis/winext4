param([int]$ProcId, [string]$Action, [string]$Name)
# Tiny UI Automation driver: list windows/buttons of a process, click a button by (partial) name.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

function All-Buttons($el) {
  $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)
}
function Label($b) {
  $n = $b.Current.Name
  if (-not $n) {
    $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $n = ($b.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc) | ForEach-Object { $_.Current.Name }) -join ''
  }
  if (-not $n) { $n = $b.Current.HelpText }; $n
}

switch ($Action) {
  'list' {
    foreach ($w in $wins) {
      "WINDOW: $($w.Current.Name)"
      # dialogs may be children of the main window
      $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
      foreach ($d in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $wc)) { "  DIALOG: $($d.Current.Name)" }
      foreach ($b in All-Buttons $w) { "  button: [$(Label $b)] enabled=$($b.Current.IsEnabled)" }
    }
  }
  'click' {
    foreach ($w in $wins) {
      foreach ($b in All-Buttons $w) {
        if ((Label $b) -like "*$Name*" -and $b.Current.IsEnabled) {
          $p = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)

          $p.Invoke()
          "clicked [$(Label $b)]"
          return
        }
      }
    }
    "button '$Name' not found"
  }
  'text' {
    $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($w in $wins) { foreach ($t in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) { $t.Current.Name } }
  }
}
