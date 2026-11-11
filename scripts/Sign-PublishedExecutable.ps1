param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$CertificateThumbprint = "89AA6D9BABBAAE6672A34DE9F07E47359389ACF2",
    [string]$PfxPath = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'
try {
    $executable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($signTool) {
        $signToolPath = $signTool.Source
    } else {
        $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' }
            elseif ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
        $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signToolPath = Get-ChildItem -LiteralPath $sdkBin -Directory |
            Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "$architecture\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $signToolPath) { throw 'SignTool not found. Install Windows SDK signing tools.' }

    $signArguments = @('sign', '/fd', 'SHA256')
    if ($PfxPath) {
        $pfx = (Resolve-Path -LiteralPath $PfxPath).ProviderPath
        $signArguments += @('/f', $pfx)
        if ($env:MEDICAL_DISC_SIGNING_PASSWORD) {
            $signArguments += @('/p', $env:MEDICAL_DISC_SIGNING_PASSWORD)
        }
    } else {
        $CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
        if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'A valid certificate thumbprint is required.' }
        $signArguments += @('/sha1', $CertificateThumbprint, '/s', 'My')
    }
    if ($TimestampUrl) {
        $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $signArguments += $executable
    # Keep the password out of MSBuild properties, command logging, and source files.
    & $signToolPath @signArguments
    if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed. Check CurrentUser/My certificate, expiry, private key, and timestamp connectivity (or PFX settings).' }
    # dotnet/MSBuild can inherit PowerShell 7 module paths; load the Windows module explicitly.
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    if (-not $signature.SignerCertificate -or $signature.Status -notin @('Valid', 'NotTrusted')) {
        throw "Signature verification failed: $($signature.Status)"
    }
    if (-not $PfxPath -and $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw 'The signed file does not match the selected certificate.'
    }
    if ($TimestampUrl -and -not $signature.TimeStamperCertificate) { throw 'Timestamp missing from the signed file.' }
    # Self-signed certificates may be untrusted until deployed to managed devices.
    Write-Host "Signed: $executable (SHA256; trust status: $($signature.Status))"
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
