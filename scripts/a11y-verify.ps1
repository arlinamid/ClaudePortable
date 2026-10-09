<#
.SYNOPSIS
    Accessibility smoke test for the AgentPortable GUI via Windows UI Automation.

.DESCRIPTION
    Launches the GUI, visits every sidebar page and checks what a screen reader
    (Narrator, NVDA, JAWS) can see:

      - every page's content is in the UI Automation tree (buttons, check
        boxes, grids), not just the sidebar
      - every interactive control has a non-empty accessible name that is not
        a WPF type name such as "System.Windows.Controls.ListBoxItem"
      - each page exposes a level-1 heading
      - a "What to back up" check box can be toggled through UI Automation and
        the change reaches settings.json

    %LOCALAPPDATA%\ClaudePortable\settings.json is backed up before the run and
    restored afterwards. Exits 0 when every check passes, 1 otherwise.

.PARAMETER Exe
    Path to claudeportable.exe. Defaults to the Debug build output.

.EXAMPLE
    pwsh scripts/a11y-verify.ps1
    pwsh scripts/a11y-verify.ps1 -Exe .\dist\portable\claudeportable.exe
#>
param(
    [string] $Exe = (Join-Path $PSScriptRoot "..\src\ClaudePortable.App\bin\Debug\net10.0-windows\claudeportable.exe")
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$UIA = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$Scope = [System.Windows.Automation.TreeScope]

$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string] $msg) { $failures.Add($msg); Write-Host "  FAIL $msg" -ForegroundColor Red }
function Pass([string] $msg) { Write-Host "  ok   $msg" -ForegroundColor Green }

# UIA_HeadingLevelPropertyId (Windows 10 1809+). The managed client has no
# constant for it; LookupById returns null on older systems.
$headingProp = [System.Windows.Automation.AutomationProperty]::LookupById(30173)
$HeadingLevel1 = 80051

$interactive = @($CT::Button, $CT::CheckBox, $CT::RadioButton, $CT::ComboBox, $CT::Edit,
                 $CT::DataGrid, $CT::List, $CT::ListItem, $CT::ProgressBar, $CT::Slider, $CT::Tab)

function Get-Window {
    for ($i = 0; $i -lt 40; $i++) {
        $p = Get-Process claudeportable -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($p) { return $UIA::FromHandle($p.MainWindowHandle) }
        Start-Sleep -Milliseconds 250
    }
    throw "AgentPortable window did not appear."
}

function Get-All($root, $condition) {
    $root.FindAll($Scope::Descendants, $condition)
}

$settings = Join-Path $env:LOCALAPPDATA 'ClaudePortable\settings.json'
$settingsBackup = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }
$started = $null

try {
    if (-not (Get-Process claudeportable -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })) {
        $started = Start-Process -FilePath $Exe -ArgumentList '--gui' -PassThru
    }
    $window = Get-Window
    Start-Sleep -Milliseconds 800

    $listItemCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::ListItem)
    $nav = Get-All $window $listItemCond | Where-Object {
        $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($_)
        $parent.Current.AutomationId -eq 'Nav'
    }
    if ($nav.Count -ne 6) { Fail "expected 6 sidebar items, found $($nav.Count)" }

    foreach ($item in $nav) {
        $page = $item.Current.Name
        Write-Host "`n[$page]" -ForegroundColor Cyan
        if ([string]::IsNullOrWhiteSpace($page) -or $page -like 'System.*') {
            Fail "sidebar item has no accessible name ('$page')"
        }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700

        $all = Get-All $window ([System.Windows.Automation.Condition]::TrueCondition)
        $onPage = @($all | Where-Object { -not $_.Current.IsOffscreen })

        # Content beyond the sidebar must be reachable.
        $contentControls = @($onPage | Where-Object {
            $_.Current.ControlType -in @($CT::CheckBox, $CT::DataGrid, $CT::Edit, $CT::RadioButton) -or
            ($_.Current.ControlType -eq $CT::List -and $_.Current.AutomationId -ne 'Nav') -or
            ($_.Current.ControlType -eq $CT::Button -and $_.Current.Name -notin @('Backup now', 'Mentés most'))
        })
        if ($contentControls.Count -eq 0) { Fail "no page controls in the automation tree" }
        else { Pass "$($contentControls.Count) page controls exposed" }

        $unnamed = @($onPage | Where-Object {
            $_.Current.ControlType -in $interactive -and
            $_.Current.ControlType -ne $CT::ListItem -and
            ([string]::IsNullOrWhiteSpace($_.Current.Name) -or $_.Current.Name -like 'System.*')
        })
        foreach ($u in $unnamed) {
            Fail "unnamed $($u.Current.ControlType.ProgrammaticName) (AutomationId '$($u.Current.AutomationId)')"
        }
        if ($unnamed.Count -eq 0) { Pass "all interactive controls named" }

        if ($null -ne $headingProp) {
            $h1 = @($onPage | Where-Object { $_.GetCurrentPropertyValue($headingProp) -eq $HeadingLevel1 })
            if ($h1.Count -ge 1) { Pass "heading level 1: '$($h1[0].Current.Name)'" }
            else { Fail "no level-1 heading" }
        }
        else {
            Write-Host "  skip heading level (not exposed by the .NET UI Automation client; check with Accessibility Insights)" -ForegroundColor DarkGray
        }
    }

    # Toggle a backup group through UI Automation and check it is persisted.
    Write-Host "`n[settings round-trip]" -ForegroundColor Cyan
    $nav[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
    $boxCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CT::CheckBox)
    $codex = Get-All $window $boxCond |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name -eq 'Codex' } | Select-Object -First 1
    if ($null -eq $codex) {
        Fail "'Codex' backup check box not found"
    }
    else {
        $toggle = $codex.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $before = $toggle.Current.ToggleState
        $toggle.Toggle()
        Start-Sleep -Milliseconds 500
        $saved = if (Test-Path $settings) { Get-Content $settings -Raw } else { '' }
        $toggle.Toggle()
        Start-Sleep -Milliseconds 300
        if ($before -eq 'On' -and $saved -match '"backupGroups"' -and $saved -notmatch '"codex"') {
            Pass "unticking Codex via UI Automation saved backupGroups without codex"
        }
        elseif ($before -eq 'Off' -and $saved -match '"codex"') {
            Pass "ticking Codex via UI Automation saved it"
        }
        else {
            Fail "check box toggle did not reach settings.json (state before: $before; settings: $saved)"
        }
    }
}
finally {
    if ($null -ne $settingsBackup) { Set-Content -Path $settings -Value $settingsBackup -NoNewline }
    elseif (Test-Path $settings) { Remove-Item $settings }
    if ($null -ne $started) { Stop-Process -Id $started.Id -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "All accessibility checks passed." -ForegroundColor Green
    exit 0
}
Write-Host "$($failures.Count) accessibility check(s) failed." -ForegroundColor Red
exit 1
