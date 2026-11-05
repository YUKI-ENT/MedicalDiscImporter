$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$result = @{ State = 'Unknown'; Reason = 'Defenderの状態・権限・検査履歴を確認できません。'; SignatureVersion = $null; ScanId = $null; StartEventId = $null; EndEventId = $null; ExitCode = $null }
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
    if (@($p.ExclusionPath).Where({ $_ -like 'N/A:*' }).Count -gt 0) {
        $result.Reason = 'Defenderの除外設定を確認する権限がありません。管理者として実行するか施設管理者に確認してください。'; throw 'InsufficientRights'
    }
    # Normal scans respect exclusions. Fail closed instead of modifying institutional policy.
    if ($p.DisableArchiveScanning -ne $false -or $p.HideExclusionsFromLocalAdmins -eq $true -or
        @($p.ExclusionPath).Where({ $_ }).Count -gt 0 -or @($p.ExclusionExtension).Where({ $_ }).Count -gt 0 -or
        @($p.ExclusionProcess).Where({ $_ }).Count -gt 0 -or @($p.ThreatIDDefaultAction_Ids).Where({ $_ }).Count -gt 0) {
        $result.Reason = 'Defenderの除外・脅威別アクション設定、またはアーカイブ検査の無効化があるため全ファイル検査を保証できません。'; throw 'ScanPolicyUnsupported'
    }
    $result.SignatureVersion = [string]$s.AntivirusSignatureVersion
}
function Event-Data($event) {
    $values = @{}
    $xml = [xml]$event.ToXml()
    foreach ($node in $xml.Event.EventData.Data) { $values[[string]$node.Name] = [string]$node.'#text' }
    return $values
}
try {
    Assert-Protection
    $baseline = (Get-WinEvent -LogName $log -MaxEvents 1 -ErrorAction Stop).RecordId
    $historyBefore = @(Get-MpThreatDetection -ErrorAction Stop)
    $began = Get-Date
    $platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    $exe = Get-ChildItem -LiteralPath $platform -Directory -ErrorAction Stop | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$exe) { $exe = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
    # Capture and discard localized output: it is not a reliable machine-readable verdict.
    $null = & $exe -Scan -ScanType 3 -File $env:MDI_SCAN_PATH 2>&1
    $result.ExitCode = $LASTEXITCODE
    Assert-Protection
    $events = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $events = @(Get-WinEvent -LogName $log -FilterXPath "*[System[EventRecordID > $baseline]]" -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        if ($eventErrors | Where-Object { $_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' }) { throw 'EventReadFailed' }
        $starts = @($events | Where-Object { $_.Id -eq 1000 -and (Event-Data $_)['Scan Resources'].TrimEnd('\') -eq $env:MDI_SCAN_PATH.TrimEnd('\') })
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
    if ($detections.Count -gt 0 -or @($events | Where-Object { $_.Id -in 1006,1007,1008,1116,1117,1118,1119 }).Count -gt 0) {
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
$result | ConvertTo-Json -Compress
