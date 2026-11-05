$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$result = @{ State = 'Unknown'; Reason = 'Defenderの状態・権限・検査履歴を確認できません。'; SignatureVersion = $null; ScanId = $null; StartEventId = $null; EndEventId = $null; ExitCode = $null; TargetFileCount = $null; ThreatDetectionCount = $null; ElapsedSeconds = $null }
$log = 'Microsoft-Windows-Windows Defender/Operational'
function Assert-Protection {
    $s = Get-MpComputerStatus -ErrorAction Stop
    if (!$s.AMServiceEnabled -or !$s.AntivirusEnabled -or !$s.RealTimeProtectionEnabled -or $s.AMRunningMode -ne 'Normal') {
        $result.Reason = 'Defenderが通常の有効状態ではありません。'; throw 'ProtectionInactive'
    }
    $age = ([DateTime]::Now - $s.AntivirusSignatureLastUpdated).TotalDays
    if (!$s.AntivirusSignatureVersion -or $age -lt 0 -or $age -gt [int]$env:MDI_SIGNATURE_DAYS) {
        $result.Reason = 'Defender定義が古い、または定義日時を確認できません。'; throw 'SignatureStale'
    }
    $p = Get-MpPreference -ErrorAction Stop
    if ($p.HideExclusionsFromLocalAdmins -eq $true -or
        @($p.ExclusionPath).Where({ $_ -like 'N/A:*' }).Count -gt 0 -or
        @($p.ExclusionExtension).Where({ $_ -like 'N/A:*' }).Count -gt 0) {
        $result.Reason = 'Defenderのファイル・拡張子除外設定が非公開のため、検査対象を確認できません。施設管理者に確認してください。'; throw 'InsufficientRights'
    }
    if ($p.DisableArchiveScanning -ne $false) {
        $result.Reason = 'Defenderのアーカイブ検査が無効、または設定を取得できません。施設管理者に確認してください。'; throw 'ArchiveScanUnavailable'
    }
    if (@($p.ThreatIDDefaultAction_Ids).Where({ $_ }).Count -gt 0) {
        $result.Reason = 'Defenderに脅威別アクションが設定されているため、検出時の処理を保証できません。施設管理者に確認してください。'; throw 'ThreatActionUnsupported'
    }
    # Process exclusions apply to real-time protection, not on-demand custom scans.
    # Do not implement Defender's wildcard/path matching ourselves. Check each actual target below.
    # Contextual exclusions require scan-trigger semantics not established by CheckExclusion alone.
    if (@($p.ExclusionPath).Where({ $_ -match '\\:\{' }).Count -gt 0) {
        $result.Reason = 'Defenderに条件付き除外が設定されています。この検査方式では適用条件を確認できません。施設管理者に確認してください。'; throw 'ContextualExclusionUnsupported'
    }
    $result.SignatureVersion = [string]$s.AntivirusSignatureVersion
}
function Assert-ScanCoverage {
    $result.Reason = 'Defenderの検査対象に対する除外確認を完了できません。出力は禁止しました。'
    # Microsoft documents CheckExclusion exit 0 = excluded, 1 = not excluded.
    # Only exactly 1 is acceptable. Discard localized output and all patient paths.
    $files = @(Get-ChildItem -LiteralPath $env:MDI_SCAN_PATH -Recurse -File -Force -ErrorAction Stop | ForEach-Object { $_.FullName })
    if ($null -eq $result.TargetFileCount) { $result.TargetFileCount = $files.Count }
    $targets = @($env:MDI_SCAN_PATH) + $files
    foreach ($target in $targets) {
        $null = & $exe -CheckExclusion -Path $target 2>&1
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            $result.Reason = '今回の検査対象フォルダ、またはファイルがDefenderの除外対象です。施設管理者に確認してください。'; throw 'ScanTargetExcluded'
        }
        if ($code -ne 1) { throw 'ExclusionCheckFailed' }
    }
}
function Event-Data($event) {
    $values = @{}
    $xml = [xml]$event.ToXml()
    foreach ($node in $xml.Event.EventData.Data) { $values[[string]$node.Name] = [string]$node.'#text' }
    return $values
}
function Test-ScanResource([string]$resource) {
    if ([string]::IsNullOrWhiteSpace($resource)) { return $false }
    $target = $env:MDI_SCAN_PATH.TrimEnd('\')
    $value = $resource.TrimEnd('\')
    # Observed Defender 1000 resources include folder:_ with escaped backslashes.
    # Accept only exact whole-resource forms of this directory, never a substring or multiple targets.
    foreach ($candidate in @($target, ('folder:_' + $target), ('folder:_' + $target.Replace('\', '\\')))) {
        if ([string]::Equals($candidate, $value, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
try {
    $platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    $exe = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction Stop | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$exe) { $exe = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
    Assert-Protection
    Assert-ScanCoverage
    $baseline = (Get-WinEvent -LogName $log -MaxEvents 1 -ErrorAction Stop).RecordId
    $historyBefore = @(Get-MpThreatDetection -ErrorAction Stop)
    $began = Get-Date
    # Capture and discard localized output: it is not a reliable machine-readable verdict.
    $null = & $exe -Scan -ScanType 3 -File $env:MDI_SCAN_PATH 2>&1
    $result.ExitCode = $LASTEXITCODE
    Assert-Protection
    Assert-ScanCoverage
    $events = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $events = @(Get-WinEvent -LogName $log -FilterXPath "*[System[EventRecordID > $baseline]]" -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        if ($eventErrors | Where-Object { $_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' }) { throw 'EventReadFailed' }
        $starts = @($events | Where-Object { $_.Id -eq 1000 -and (Test-ScanResource (Event-Data $_)['Scan Resources']) })
        if ($starts.Count -eq 1) {
            $scanId = (Event-Data $starts[0])['Scan ID']
            $ends = @($events | Where-Object { $_.Id -eq 1001 -and (Event-Data $_)['Scan ID'] -eq $scanId -and $_.RecordId -gt $starts[0].RecordId })
            if ($ends.Count -eq 1) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    $historyAfter = @(Get-MpThreatDetection -ErrorAction Stop)
    # Conservatively block on any detection during the scan, even elsewhere on this endpoint.
    $detections = @($historyAfter | Where-Object {
        $_.InitialDetectionTime -ge $began -or $_.LastThreatStatusChangeTime -ge $began -or
        $_.DetectionID -notin @($historyBefore | ForEach-Object { $_.DetectionID }) -or
        @($_.Resources).Where({ $_ -like ('*' + $env:MDI_SCAN_PATH + '*') }).Count -gt 0
    })
    # Count unique detection records, not event entries (detection/remediation can log several events).
    # Event-only detection cannot establish an exact count and must not be reported as zero.
    $threatEvents = @($events | Where-Object { $_.Id -in 1006,1007,1008,1116,1117,1118,1119 })
    if ($detections.Count -gt 0) {
        if (@($detections | Where-Object { !$_.DetectionID }).Count -eq 0) {
            $result.ThreatDetectionCount = @($detections | ForEach-Object { [string]$_.DetectionID } | Select-Object -Unique).Count
        }
    } elseif ($threatEvents.Count -eq 0) { $result.ThreatDetectionCount = 0 }
    if ($detections.Count -gt 0 -or $threatEvents.Count -gt 0) {
        $result.State = 'ThreatDetected'; $result.Reason = 'Defenderが脅威を検出しました。修復済みの場合も出力は禁止します。'
    } elseif ($result.ExitCode -ne 0 -or $starts.Count -ne 1 -or $ends.Count -ne 1 -or !$scanId -or
        @($events | Where-Object { $_.Id -in 1002,1005,5001,5007,5008,5009,5010,5012 }).Count -gt 0) {
        $result.Reason = '検査の正常完了、対象フォルダ、または保護設定の維持を確認できません。'
    } else {
        $result.State = 'Passed'; $result.Reason = '検査完了・脅威検出なし'
        $result.ScanId = $scanId; $result.StartEventId = $starts[0].RecordId; $result.EndEventId = $ends[0].RecordId
    }
} catch {
    # Keep a fixed reason; exception messages can contain patient data.
}
$result.ElapsedSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
$result | ConvertTo-Json -Compress
