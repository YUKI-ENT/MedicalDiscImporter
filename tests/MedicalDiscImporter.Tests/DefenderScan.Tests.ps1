# Exercise the production verdict script against synthetic OS responses, without scanning,
# elevating, creating threats, or changing Defender policy. No test switch exists in production.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../../src/MedicalDiscImporter.Core/DefenderScan.ps1'
$production = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
$invocation = '$null = & $exe -Scan -ScanType 3 -File $env:MDI_SCAN_PATH 2>&1'
if (!$production.Contains($invocation)) { throw 'Production invocation changed; update test harness explicitly.' }
$isolated = $production.Replace($invocation, '$global:scanCalls++; $global:LASTEXITCODE = $global:scanExit')
$checkInvocation = '$null = & $exe -CheckExclusion -Path $target 2>&1'
if (!$isolated.Contains($checkInvocation)) { throw 'Production exclusion check changed; update test harness explicitly.' }
$isolated = $isolated.Replace($checkInvocation, '$global:checkedTargets += $target; $global:LASTEXITCODE = Get-TestExclusionCode')
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
        ExclusionExtension = $global:extensions; ExclusionProcess = $global:processes; ThreatIDDefaultAction_Ids = $global:actions;
        HideExclusionsFromLocalAdmins = $global:hideAdmins }
}
function Get-TestExclusionCode {
    if ($global:scanCalls -gt 0 -and $global:excludeAfterScan) { return 0 }
    if ($global:excludeNested -and $global:checkedTargets[-1] -like '*nested*') { return 0 }
    return $global:checkExit
}
function Get-MpThreatDetection {
    [CmdletBinding()] param()
    $global:historyCalls++
    if ($global:historyFailure) { throw 'Synthetic history access failure' }
    if ($global:historyCalls -gt 1 -and $global:threat) {
        $record = [pscustomobject]@{ InitialDetectionTime = Get-Date; LastThreatStatusChangeTime = Get-Date; DetectionID = 'synthetic'; Resources = @('synthetic') }
        $record
        if ($global:duplicateThreat) { $record }
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
    [CmdletBinding()] param($LiteralPath, [switch]$Directory, [switch]$File, [switch]$Recurse, [switch]$Force)
    if ($Directory) { [pscustomobject]@{ Name = 'synthetic'; FullName = 'C:\MDI-Synthetic-Platform' } }
    else {
        [pscustomobject]@{ FullName = (Join-Path $env:MDI_SCAN_PATH 'clean.dat') }
        [pscustomobject]@{ FullName = (Join-Path $env:MDI_SCAN_PATH 'nested\日本語.dcm') }
    }
}
function Test-Path { [CmdletBinding()] param($LiteralPath) return $true }

$failures = 0
$cases = @(
    @{ Name = 'clean completed scan passes'; Expected = 'Passed' },
    @{ Name = 'actual Defender escaped folder resource passes'; Expected = 'Passed'; Change = { $global:resource = 'folder:_' + $env:MDI_SCAN_PATH.Replace('\', '\\') } },
    @{ Name = 'folder resource without escaping passes'; Expected = 'Passed'; Change = { $global:resource = 'folder:_' + $env:MDI_SCAN_PATH } },
    @{ Name = 'multiple resources cannot pass'; Expected = 'Unknown'; Change = { $global:resource = 'folder:_' + $env:MDI_SCAN_PATH + ';C:\other' } },
    @{ Name = 'similar target prefix cannot pass'; Expected = 'Unknown'; Change = { $global:resource = 'folder:_' + $env:MDI_SCAN_PATH + '-other' } },
    @{ Name = 'disabled protection blocks'; Expected = 'Unknown'; Change = { $global:enabled = $false } },
    @{ Name = 'stale signatures block'; Expected = 'Unknown'; Change = { $global:signatureAge = 8 } },
    @{ Name = 'future signatures block'; Expected = 'Unknown'; Change = { $global:signatureAge = -1 } },
    @{ Name = 'unrelated folder exclusion permits verified targets'; Expected = 'Passed'; Change = { $global:exclusions = @('C:\excluded') } },
    @{ Name = 'unrelated extension exclusion permits verified targets'; Expected = 'Passed'; Change = { $global:extensions = @('obj') } },
    @{ Name = 'process exclusion does not block on-demand scan'; Expected = 'Passed'; Change = { $global:processes = @('devenv.exe') } },
    @{ Name = 'excluded root blocks before scan'; Expected = 'Unknown'; NoScan = $true; Reason = '今回の検査対象'; Change = { $global:checkExit = 0 } },
    @{ Name = 'excluded nested file blocks before scan'; Expected = 'Unknown'; NoScan = $true; Reason = '今回の検査対象'; Change = { $global:excludeNested = $true } },
    @{ Name = 'exclusion checker failure blocks before scan'; Expected = 'Unknown'; NoScan = $true; Reason = '除外確認を完了できません'; Change = { $global:checkExit = 5 } },
    @{ Name = 'exclusion checker access error blocks'; Expected = 'Unknown'; NoScan = $true; Change = { $global:checkExit = -2147024891 } },
    @{ Name = 'exclusion introduced after scan blocks'; Expected = 'Unknown'; Change = { $global:excludeAfterScan = $true } },
    @{ Name = 'contextual exclusion blocks conservatively'; Expected = 'Unknown'; NoScan = $true; Reason = '条件付き除外'; Change = { $global:exclusions = @('C:\test\:{ScanTrigger:OnDemand}') } },
    @{ Name = 'hidden extension exclusions block'; Expected = 'Unknown'; NoScan = $true; Reason = '非公開'; Change = { $global:extensions = @('N/A: Must be an administrator') } },
    @{ Name = 'admin-hidden exclusions block'; Expected = 'Unknown'; NoScan = $true; Reason = '非公開'; Change = { $global:hideAdmins = $true } },
    @{ Name = 'threat action policy blocks with explicit reason'; Expected = 'Unknown'; NoScan = $true; Reason = '脅威別アクション'; Change = { $global:actions = @(123) } },
    @{ Name = 'hidden exclusions block'; Expected = 'Unknown'; Change = { $global:exclusions = @('N/A: Must be an administrator to view exclusions') } },
    @{ Name = 'archive scanning disabled blocks'; Expected = 'Unknown'; Change = { $global:archiveDisabled = $true } },
    @{ Name = 'missing completion blocks'; Expected = 'Unknown'; Change = { $global:complete = $false } },
    @{ Name = 'different Scan ID blocks'; Expected = 'Unknown'; Change = { $global:endScanId = 'other-scan' } },
    @{ Name = 'different target blocks'; Expected = 'Unknown'; Change = { $global:resource = 'C:\other-target' } },
    @{ Name = 'nonzero exit blocks'; Expected = 'Unknown'; Change = { $global:scanExit = 2 } },
    @{ Name = 'history retrieval failure blocks'; Expected = 'Unknown'; Change = { $global:historyFailure = $true } },
    @{ Name = 'detected and remediated with exit zero blocks'; Expected = 'ThreatDetected'; ExpectedThreatCount = 1; Change = { $global:threat = $true } },
    @{ Name = 'duplicate detection records count once'; Expected = 'ThreatDetected'; ExpectedThreatCount = 1; Change = { $global:threat = $true; $global:duplicateThreat = $true } },
    @{ Name = 'detection event blocks without inventing an exact count'; Expected = 'ThreatDetected'; UnknownThreatCount = $true; Change = { $global:extraEvent = 1116 } },
    @{ Name = 'cancel event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 1002 } },
    @{ Name = 'error event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 1005 } },
    @{ Name = 'configuration change event blocks'; Expected = 'Unknown'; Change = { $global:extraEvent = 5007 } }
)
foreach ($case in $cases) {
    $global:enabled = $true; $global:signatureAge = 1; $global:exclusions = @(); $global:archiveDisabled = $false
    $global:extensions = @(); $global:processes = @(); $global:actions = @(); $global:hideAdmins = $false
    $global:checkExit = 1; $global:excludeNested = $false; $global:excludeAfterScan = $false; $global:checkedTargets = @(); $global:scanCalls = 0
    $global:duplicateThreat = $false
    $global:complete = $true; $global:resource = $env:MDI_SCAN_PATH; $global:endScanId = 'scan-test'
    $global:scanExit = 0; $global:historyFailure = $false; $global:threat = $false; $global:historyCalls = 0; $global:extraEvent = 0
    if ($case.Change) { & $case.Change }
    $verdict = (& ([scriptblock]::Create($isolated))) | ConvertFrom-Json
    $checksPass = $verdict.State -eq $case.Expected
    if ($case.Reason -and !$verdict.Reason.Contains($case.Reason)) { $checksPass = $false }
    if ($case.NoScan -and $global:scanCalls -ne 0) { $checksPass = $false }
    if ($verdict.State -eq 'Passed' -and ($global:checkedTargets.Count -ne 6 -or $global:scanCalls -ne 1)) { $checksPass = $false }
    if ($verdict.State -eq 'Passed' -and ($verdict.TargetFileCount -ne 2 -or $verdict.ThreatDetectionCount -ne 0)) { $checksPass = $false }
    if ($null -eq $verdict.ElapsedSeconds -or $verdict.ElapsedSeconds -lt 0) { $checksPass = $false }
    if ($case.ContainsKey('ExpectedThreatCount') -and $verdict.ThreatDetectionCount -ne $case.ExpectedThreatCount) { $checksPass = $false }
    if ($case.UnknownThreatCount -and $null -ne $verdict.ThreatDetectionCount) { $checksPass = $false }
    if ($checksPass) { Write-Host "PASS $($case.Name)" }
    else { $failures++; Write-Host "FAIL $($case.Name): $($verdict | ConvertTo-Json -Compress)" }
}
Write-Host "Synthetic Defender verdict tests: $($cases.Count); failures: $failures"
if ($failures) { exit 1 }
