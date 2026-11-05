# Exercise the production verdict script against synthetic OS responses, without scanning,
# elevating, creating threats, or changing Defender policy. No test switch exists in production.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../../src/MedicalDiscImporter.Core/DefenderScan.ps1'
$production = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
$invocation = '$null = & $exe -Scan -ScanType 3 -File $env:MDI_SCAN_PATH 2>&1'
if (!$production.Contains($invocation)) { throw 'Production invocation changed; update test harness explicitly.' }
$isolated = $production.Replace($invocation, '$global:LASTEXITCODE = $global:scanExit')
if ($isolated.Contains('& $exe')) { throw 'Refusing to execute real scanner in synthetic test.' }
$env:MDI_SCAN_PATH = 'C:\MDI-Synthetic-Test'
$env:MDI_SIGNATURE_DAYS = '7'

function Get-MpComputerStatus {
    [CmdletBinding()] param()
    [pscustomobject]@{ AMServiceEnabled = $global:enabled; AntivirusEnabled = $true; RealTimeProtectionEnabled = $true;
        AMRunningMode = 'Normal'; AntivirusSignatureVersion = '1.2.3'; AntivirusSignatureLastUpdated = (Get-Date).AddDays(-$global:signatureAge) }
}
function Get-MpPreference {
    [CmdletBinding()] param()
    [pscustomobject]@{ DisableArchiveScanning = $global:archiveDisabled; ExclusionPath = $global:exclusions;
        ExclusionExtension = @(); ExclusionProcess = @(); ThreatIDDefaultAction_Ids = @() }
}
function Get-MpThreatDetection {
    [CmdletBinding()] param()
    $global:historyCalls++
    if ($global:historyFailure) { throw 'Synthetic history access failure' }
    if ($global:historyCalls -gt 1 -and $global:threat) {
        [pscustomobject]@{ InitialDetectionTime = Get-Date; LastThreatStatusChangeTime = Get-Date; DetectionID = 'synthetic'; Resources = @('synthetic') }
    }
}
function New-TestEvent($id, $record, $resource, $scanId) {
    $event = [pscustomobject]@{ Id = $id; RecordId = $record; Xml = @"
<Event><EventData><Data Name="Scan ID">$scanId</Data><Data Name="Scan Resources">$resource</Data></EventData></Event>
"@ }
    $event | Add-Member -MemberType ScriptMethod -Name ToXml -Value { $this.Xml }
    return $event
}
function Get-WinEvent {
    [CmdletBinding()] param($LogName, $MaxEvents, $FilterXPath)
    if ($MaxEvents) { return [pscustomobject]@{ RecordId = 100 } }
    New-TestEvent 1000 101 $global:resource 'scan-test'
    if ($global:complete) { New-TestEvent 1001 102 '' $global:endScanId }
    if ($global:extraEvent) { New-TestEvent $global:extraEvent 103 '' 'scan-test' }
}
function Start-Sleep { param($Milliseconds) }
function Get-ChildItem {
    [CmdletBinding()] param($LiteralPath, [switch]$Directory)
    [pscustomobject]@{ Name = 'synthetic'; FullName = 'C:\MDI-Synthetic-Platform' }
}
function Test-Path { [CmdletBinding()] param($LiteralPath) return $true }

$failures = 0
$cases = @(
    @{ Name = 'clean completed scan passes'; Expected = 'Passed' },
    @{ Name = 'disabled protection blocks'; Expected = 'Unknown'; Change = { $global:enabled = $false } },
    @{ Name = 'stale signatures block'; Expected = 'Unknown'; Change = { $global:signatureAge = 8 } },
    @{ Name = 'future signatures block'; Expected = 'Unknown'; Change = { $global:signatureAge = -1 } },
    @{ Name = 'exclusion blocks'; Expected = 'Unknown'; Change = { $global:exclusions = @('C:\excluded') } },
    @{ Name = 'hidden exclusions block'; Expected = 'Unknown'; Change = { $global:exclusions = @('N/A: Must be an administrator to view exclusions') } },
    @{ Name = 'archive scanning disabled blocks'; Expected = 'Unknown'; Change = { $global:archiveDisabled = $true } },
    @{ Name = 'missing completion blocks'; Expected = 'Unknown'; Change = { $global:complete = $false } },
    @{ Name = 'different Scan ID blocks'; Expected = 'Unknown'; Change = { $global:endScanId = 'other-scan' } },
    @{ Name = 'different target blocks'; Expected = 'Unknown'; Change = { $global:resource = 'C:\other-target' } },
    @{ Name = 'nonzero exit blocks'; Expected = 'Unknown'; Change = { $global:scanExit = 2 } },
    @{ Name = 'history retrieval failure blocks'; Expected = 'Unknown'; Change = { $global:historyFailure = $true } },
    @{ Name = 'detected and remediated with exit zero blocks'; Expected = 'ThreatDetected'; Change = { $global:threat = $true } },
    @{ Name = 'detection event blocks'; Expected = 'ThreatDetected'; Change = { $global:extraEvent = 1116 } },
    @{ Name = 'cancel event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 1002 } },
    @{ Name = 'error event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 1005 } },
    @{ Name = 'configuration change event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 5007 } }
)
foreach ($case in $cases) {
    $global:enabled = $true; $global:signatureAge = 1; $global:exclusions = @(); $global:archiveDisabled = $false
    $global:complete = $true; $global:resource = $env:MDI_SCAN_PATH; $global:endScanId = 'scan-test'
    $global:scanExit = 0; $global:historyFailure = $false; $global:threat = $false; $global:historyCalls = 0; $global:extraEvent = 0
    if ($case.Change) { & $case.Change }
    $verdict = (& ([scriptblock]::Create($isolated))) | ConvertFrom-Json
    if ($verdict.State -eq $case.Expected) { Write-Host "PASS $($case.Name)" }
    else { $failures++; Write-Host "FAIL $($case.Name): $($verdict | ConvertTo-Json -Compress)" }
}
Write-Host "Synthetic Defender verdict tests: $($cases.Count); failures: $failures"
if ($failures) { exit 1 }
