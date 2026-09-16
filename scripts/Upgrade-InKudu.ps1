#Requires -Version 7.0

# **Write-Host は意図して使っている。** Kudu の運用者が進行状況を追うための出力であり、
# パイプラインへ渡すデータではない
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = '運用者へ進み具合を見せるため')]

<#
.SYNOPSIS
Kudu 上から Questionnaire を更新またはロールバックします。

.DESCRIPTION
GitHub Release または手動配置した ZIP と SHA-256 を検証し、site\wwwroot の控えを作成してから
app_offline.htm でアプリを停止し、配置内容を丸ごと入れ替えます。更新時は新版 DLL で DB
マイグレーションを実行し、最後に /healthz と /ready を確認します。

このスクリプトは %HOME%\data に置いて PowerShell 7 で実行してください。site\wwwroot や
%SystemDrive%\local からは実行できません。

.PARAMETER Version
配置する Release の版です。v1.2.3 と 1.2.3 のどちらも指定できます。省略時は最新 Release を使います。

.PARAMETER PackagePath
外部へ接続できない環境で使う、手動配置済みの配置用 ZIP のパスです。

.PARAMETER ChecksumPath
PackagePath に対応する .sha256 のパスです。省略時は PackagePath に .sha256 を加えたパスを使います。

.PARAMETER Rollback
配置し直す控え ZIP のパスです。DB は戻しません。

.PARAMETER BaseUri
ヘルスチェック対象の URL です。省略時は WEBSITE_HOSTNAME から HTTPS の URL を組み立てます。

.PARAMETER BackupRetentionCount
%HOME%\data\backup に残す控えの世代数です。既定は 5 世代です。

.EXAMPLE
pwsh .\Upgrade-InKudu.ps1

.EXAMPLE
pwsh .\Upgrade-InKudu.ps1 -Version 1.2.3 -BackupRetentionCount 3

.EXAMPLE
pwsh .\Upgrade-InKudu.ps1 -PackagePath .\packages\VehicleVision.PleasanterTools.Questionnaire-1.2.3.zip

.EXAMPLE
pwsh .\Upgrade-InKudu.ps1 -Rollback .\backup\Questionnaire-wwwroot-20260916-120000000.zip
#>
[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(ParameterSetName = 'Release')]
    [ValidatePattern('^v?\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory, ParameterSetName = 'Package')]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [Parameter(ParameterSetName = 'Package')]
    [ValidateNotNullOrEmpty()]
    [string]$ChecksumPath,

    [Parameter(Mandatory, ParameterSetName = 'Rollback')]
    [ValidateNotNullOrEmpty()]
    [string]$Rollback,

    [ValidateNotNullOrEmpty()]
    [uri]$BaseUri,

    [ValidateRange(1, 100)]
    [int]$BackupRetentionCount = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptVersion = '1.0.0'
$applicationDll = 'VehicleVision.PleasanterTools.Questionnaire.Web.dll'
$offlineFileName = 'app_offline.htm'

function Get-HttpStatusCode {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $responseProperty = $ErrorRecord.Exception.PSObject.Properties['Response']
    if ($null -eq $responseProperty -or $null -eq $responseProperty.Value) {
        return $null
    }

    return [int]$responseProperty.Value.StatusCode
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Description was not found: $resolvedPath"
    }

    return $resolvedPath
}

function Assert-ZipArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [switch]$RequireApplication
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -eq 0) {
            throw "ZIP archive is empty: $Path"
        }

        $hasApplication = $false
        foreach ($entry in $archive.Entries) {
            $entryPath = $entry.FullName.Replace('\', '/')
            $segments = $entryPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
            if (
                $entryPath.StartsWith('/') -or
                $entryPath -match '^[A-Za-z]:' -or
                $segments -contains '..'
            ) {
                throw "ZIP archive contains an unsafe path: $($entry.FullName)"
            }

            if ($entryPath -eq $script:applicationDll) {
                $hasApplication = $true
            }
        }

        if ($RequireApplication -and -not $hasApplication) {
            throw "ZIP archive does not contain $script:applicationDll at its root: $Path"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,

        [Parameter(Mandatory)]
        [string]$Sha256Path
    )

    $checksumLine = Get-Content -LiteralPath $Sha256Path |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    if ($null -eq $checksumLine -or $checksumLine -notmatch '^\s*([0-9A-Fa-f]{64})(?:\s+.+)?\s*$') {
        throw "SHA-256 file has an invalid format: $Sha256Path"
    }

    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 verification failed: $ArchivePath"
    }

    Write-Host "SHA-256 verification succeeded: $ArchivePath"
}

function Get-ReleasePackage {
    param(
        [string]$RequestedVersion,

        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    if ($RequestedVersion) {
        $normalizedVersion = $RequestedVersion.TrimStart('v')
    }
    else {
        Write-Host 'Resolving the latest GitHub Release.'
        $latestRelease = Invoke-RestMethod `
            -Uri 'https://api.github.com/repos/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire/releases/latest'
        $normalizedVersion = ([string]$latestRelease.tag_name).TrimStart('v')
        if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
            throw "Latest Release tag is not a supported version: $($latestRelease.tag_name)"
        }
    }

    $assetName = "VehicleVision.PleasanterTools.Questionnaire-$normalizedVersion.zip"
    $archivePath = Join-Path $DestinationDirectory $assetName
    $sha256Path = "$archivePath.sha256"
    $releaseBaseUri =
        "https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire/releases/download/v$normalizedVersion"

    Write-Host "Application version: $normalizedVersion"
    Write-Host "Downloading release package: $assetName"
    Invoke-WebRequest -Uri "$releaseBaseUri/$assetName" -OutFile $archivePath
    Invoke-WebRequest -Uri "$releaseBaseUri/$assetName.sha256" -OutFile $sha256Path

    return @{
        ArchivePath = $archivePath
        Sha256Path = $sha256Path
    }
}

function New-WwwRootBackup {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = '呼び出し元で更新処理全体を制御する内部関数のため')]
    param(
        [Parameter(Mandatory)]
        [string]$WwwRoot,

        [Parameter(Mandatory)]
        [string]$BackupDirectory
    )

    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $backupPath = Join-Path $BackupDirectory "Questionnaire-wwwroot-$timestamp.zip"
    $temporaryBackupPath = "$backupPath.tmp"

    Write-Host "Creating wwwroot backup: $backupPath"
    try {
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $WwwRoot,
            $temporaryBackupPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false
        )

        if (-not (Test-Path -LiteralPath $temporaryBackupPath -PathType Leaf) -or
            (Get-Item -LiteralPath $temporaryBackupPath).Length -eq 0) {
            throw "wwwroot backup was not created: $backupPath"
        }

        Assert-ZipArchive -Path $temporaryBackupPath -RequireApplication
        Move-Item -LiteralPath $temporaryBackupPath -Destination $backupPath
    }
    catch {
        if (Test-Path -LiteralPath $temporaryBackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryBackupPath -Force
        }
        throw
    }

    Write-Host 'The backup can contain secrets. Store or delete it securely.'
    return $backupPath
}

function Remove-ExpiredBackup {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'このスクリプトが作成した期限切れの控えだけを削除する内部関数のため')]
    param(
        [Parameter(Mandatory)]
        [string]$BackupDirectory,

        [Parameter(Mandatory)]
        [int]$RetentionCount
    )

    $expiredBackups = Get-ChildItem -LiteralPath $BackupDirectory -File `
        -Filter 'Questionnaire-wwwroot-*.zip' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip $RetentionCount

    foreach ($expiredBackup in $expiredBackups) {
        Write-Host "Deleting expired backup: $($expiredBackup.FullName)"
        Remove-Item -LiteralPath $expiredBackup.FullName -Force
    }
}

function Set-AppOffline {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = '更新処理中の IIS 停止を担う内部関数のため')]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    # **IIS にプロセスを解放させる。** Kudu には az が無く、実行中の DLL は
    # そのままでは置き換えられない
    $content = @'
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Maintenance</title></head>
<body><h1>Maintenance in progress</h1><p>Please try again later.</p></body>
</html>
'@
    Set-Content -LiteralPath $Path -Value $content -Encoding utf8NoBOM
    Write-Host 'Application offline mode enabled.'
}

function Copy-LocalParameterFile {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,

        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        return
    }

    $localFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Filter '*.local.json')
    if ($localFiles.Count -eq 0) {
        return
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    foreach ($localFile in $localFiles) {
        Copy-Item -LiteralPath $localFile.FullName -Destination $DestinationDirectory -Force
    }
}

function Clear-WwwRoot {
    param(
        [Parameter(Mandatory)]
        [string]$WwwRoot
    )

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $WwwRoot -Force |
                Where-Object { $_.Name -ne $script:offlineFileName } |
                Remove-Item -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 12) {
                throw
            }

            Write-Host "wwwroot is still locked. Retrying in 5 seconds (attempt $attempt of 12)."
            Start-Sleep -Seconds 5
        }
    }
}

function Expand-AndReplaceWwwRoot {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,

        [Parameter(Mandatory)]
        [string]$StagingDirectory,

        [Parameter(Mandatory)]
        [string]$WwwRoot
    )

    New-Item -ItemType Directory -Path $StagingDirectory | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $StagingDirectory
    if (-not (Test-Path -LiteralPath (Join-Path $StagingDirectory $script:applicationDll) -PathType Leaf)) {
        throw "Expanded package does not contain $script:applicationDll at its root."
    }

    Clear-WwwRoot -WwwRoot $WwwRoot
    Get-ChildItem -LiteralPath $StagingDirectory -Force |
        Copy-Item -Destination $WwwRoot -Recurse -Force

    if (-not (Test-Path -LiteralPath (Join-Path $WwwRoot $script:applicationDll) -PathType Leaf)) {
        throw "Application DLL was not deployed to wwwroot."
    }
}

function Invoke-ApplicationCommand {
    param(
        [Parameter(Mandatory)]
        [string]$WwwRoot,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Description
    )

    Write-Host "$Description."
    Push-Location $WwwRoot
    try {
        & dotnet ".\$script:applicationDll" @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Wait-ForEndpoint {
    param(
        [Parameter(Mandatory)]
        [uri]$Uri,

        [Parameter(Mandatory)]
        [string]$Label
    )

    # **最後の理由を残す。** /ready は DB 障害時に 503 を返すため、
    # 回数だけでなく復旧判断に使える HTTP 状態も表示する
    $lastReason = 'no response'
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 30
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                Write-Host "$Label succeeded on attempt $attempt."
                return
            }

            $lastReason = "HTTP $($response.StatusCode)"
        }
        catch {
            $statusCode = Get-HttpStatusCode -ErrorRecord $_
            $lastReason = if ($null -ne $statusCode) { "HTTP $statusCode" } else { $_.Exception.Message }
        }

        if ($attempt -lt 12) {
            Write-Host "$Label attempt $attempt failed ($lastReason). Retrying in 10 seconds."
            Start-Sleep -Seconds 10
        }
    }

    throw "$Label failed after 12 attempts ($lastReason): $Uri"
}

function Test-Deployment {
    param(
        [Parameter(Mandatory)]
        [uri]$ApplicationBaseUri
    )

    $normalizedBaseUri = $ApplicationBaseUri.AbsoluteUri.TrimEnd('/')
    Wait-ForEndpoint -Uri "$normalizedBaseUri/healthz" -Label 'Liveness check (/healthz)'
    Wait-ForEndpoint -Uri "$normalizedBaseUri/ready" -Label 'Readiness check (/ready)'
}

Write-Host "Upgrade script version: $scriptVersion"

if ([string]::IsNullOrWhiteSpace($env:HOME)) {
    throw 'HOME environment variable is not set.'
}

$homePath = [System.IO.Path]::GetFullPath($env:HOME)
$dataDirectory = Join-Path $homePath 'data'
$wwwRoot = Join-Path $homePath 'site\wwwroot'
$backupDirectory = Join-Path $dataDirectory 'backup'
$scriptPath = [System.IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$expectedScriptDirectory = [System.IO.Path]::GetFullPath($dataDirectory).TrimEnd('\')
$actualScriptDirectory = [System.IO.Path]::GetDirectoryName($scriptPath).TrimEnd('\')

if ($actualScriptDirectory -ne $expectedScriptDirectory) {
    throw "This script must be placed directly in HOME\data: $expectedScriptDirectory"
}
if (-not (Test-Path -LiteralPath $wwwRoot -PathType Container)) {
    throw "wwwroot directory was not found: $wwwRoot"
}
if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found in PATH.'
}

if ($null -eq $BaseUri) {
    if ([string]::IsNullOrWhiteSpace($env:WEBSITE_HOSTNAME)) {
        throw 'BaseUri was not specified and WEBSITE_HOSTNAME is not set.'
    }
    $BaseUri = [uri]"https://$($env:WEBSITE_HOSTNAME)"
}

New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$lockPath = Join-Path $dataDirectory 'Questionnaire-upgrade.lock'
$lockStream = $null
$workingDirectory = $null
$offlinePath = Join-Path $wwwRoot $offlineFileName
$offlineEnabled = $false
$createdBackupPath = $null

try {
    # **%HOME% は全インスタンスで共有される。** 同時更新による wwwroot の混在を
    # OS の排他ロックで防ぎ、異常終了後はファイルが残っても次回取得できるようにする
    try {
        $lockStream = [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
    }
    catch [System.IO.IOException] {
        throw 'Another update or rollback is already running.'
    }

    $workingDirectory = Join-Path $dataDirectory "upgrade-work-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workingDirectory | Out-Null

    if ($PSCmdlet.ParameterSetName -eq 'Rollback') {
        $archivePath = Resolve-ExistingFile -Path $Rollback -Description 'Rollback backup'
        if ([System.IO.Path]::GetExtension($archivePath) -ne '.zip') {
            throw "Rollback backup must be a ZIP file: $archivePath"
        }
        Assert-ZipArchive -Path $archivePath -RequireApplication

        Write-Host 'WARNING: Rollback restores application files only. The database will not be rolled back.'
        Write-Host "Restoring backup: $archivePath"
        $createdBackupPath = New-WwwRootBackup `
            -WwwRoot $wwwRoot `
            -BackupDirectory $backupDirectory
        Remove-ExpiredBackup `
            -BackupDirectory $backupDirectory `
            -RetentionCount $BackupRetentionCount
        Set-AppOffline -Path $offlinePath
        $offlineEnabled = $true
        Start-Sleep -Seconds 5
        Expand-AndReplaceWwwRoot `
            -ArchivePath $archivePath `
            -StagingDirectory (Join-Path $workingDirectory 'staging') `
            -WwwRoot $wwwRoot
        Remove-Item -LiteralPath $offlinePath -Force
        $offlineEnabled = $false
        Write-Host 'Application offline mode disabled.'
        Test-Deployment -ApplicationBaseUri $BaseUri
        Write-Host 'Rollback completed.'
        Write-Host 'WARNING: The database was not rolled back.'
        return
    }

    if ($PSCmdlet.ParameterSetName -eq 'Package') {
        $archivePath = Resolve-ExistingFile -Path $PackagePath -Description 'Package'
        $resolvedChecksumPath = if ($ChecksumPath) {
            Resolve-ExistingFile -Path $ChecksumPath -Description 'SHA-256 file'
        }
        else {
            Resolve-ExistingFile -Path "$archivePath.sha256" -Description 'SHA-256 file'
        }
        Write-Host "Using manually supplied package: $archivePath"
    }
    else {
        $releasePackage = Get-ReleasePackage `
            -RequestedVersion $Version `
            -DestinationDirectory $workingDirectory
        $archivePath = $releasePackage.ArchivePath
        $resolvedChecksumPath = $releasePackage.Sha256Path
    }

    Test-Sha256 -ArchivePath $archivePath -Sha256Path $resolvedChecksumPath
    Assert-ZipArchive -Path $archivePath -RequireApplication

    $createdBackupPath = New-WwwRootBackup `
        -WwwRoot $wwwRoot `
        -BackupDirectory $backupDirectory
    Remove-ExpiredBackup `
        -BackupDirectory $backupDirectory `
        -RetentionCount $BackupRetentionCount

    $parameterDirectory = Join-Path $wwwRoot 'App_Data\Parameters'
    $preservedParameterDirectory = Join-Path $workingDirectory 'local-parameters'
    Copy-LocalParameterFile `
        -SourceDirectory $parameterDirectory `
        -DestinationDirectory $preservedParameterDirectory

    Set-AppOffline -Path $offlinePath
    $offlineEnabled = $true
    Start-Sleep -Seconds 5

    Write-Host 'Replacing wwwroot.'
    Expand-AndReplaceWwwRoot `
        -ArchivePath $archivePath `
        -StagingDirectory (Join-Path $workingDirectory 'staging') `
        -WwwRoot $wwwRoot
    Copy-LocalParameterFile `
        -SourceDirectory $preservedParameterDirectory `
        -DestinationDirectory $parameterDirectory

    Invoke-ApplicationCommand `
        -WwwRoot $wwwRoot `
        -Arguments @('--migrate', '--wait-for-db=180') `
        -Description 'Running database migration'
    Invoke-ApplicationCommand `
        -WwwRoot $wwwRoot `
        -Arguments @('--migrate-status') `
        -Description 'Checking database migration status'

    Remove-Item -LiteralPath $offlinePath -Force
    $offlineEnabled = $false
    Write-Host 'Application offline mode disabled.'

    Test-Deployment -ApplicationBaseUri $BaseUri
    Write-Host 'Update completed.'
    Write-Host "Backup created: $createdBackupPath"
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    if ($offlineEnabled) {
        Write-Host "Application offline mode remains enabled: $offlinePath"
        if ($createdBackupPath) {
            Write-Host "Run this script with -Rollback to restore files: $createdBackupPath"
            Write-Host 'WARNING: Rollback does not restore the database.'
        }
    }
    exit 1
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
    if ($null -ne $workingDirectory -and (Test-Path -LiteralPath $workingDirectory)) {
        try {
            Remove-Item -LiteralPath $workingDirectory -Recurse -Force
        }
        catch {
            Write-Warning "Unable to delete temporary directory: $workingDirectory"
        }
    }
}
