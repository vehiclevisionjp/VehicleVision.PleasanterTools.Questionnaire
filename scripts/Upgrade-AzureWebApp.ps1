#Requires -Version 7.0

# **Write-Host は意図して使っている。** これは運用者が見ながら流す道具で、
# 進み具合を人へ見せるのが目的。戻り値として拾わせたい出力ではない
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = '運用者へ進み具合を見せるため')]

<#
.SYNOPSIS
Azure App Service 上の Questionnaire を更新またはロールバックします。

.DESCRIPTION
GitHub Release の配置用 ZIP を検証して Zip Deploy し、必要に応じて DB マイグレーションを実行します。
更新前には site/wwwroot を Kudu API から ZIP として控えます。ロールバックは控えを配置し直しますが、
DB は戻しません。

.PARAMETER ResourceGroup
対象 Web App がある Azure リソースグループ名です。

.PARAMETER WebApp
対象 Web App 名です。

.PARAMETER Slot
配置対象のスロット名です。省略時は運用スロットを対象にします。

.PARAMETER Version
配置する Release の版です。v1.2.3 と 1.2.3 のどちらも指定できます。省略時は最新 Release を使います。

.PARAMETER BackupDirectory
更新前の site/wwwroot の控えを保存するディレクトリです。

.PARAMETER SkipMigration
DB マイグレーションを実行しません。

.PARAMETER Rollback
配置し直す控え ZIP のパスです。DB は戻しません。

.EXAMPLE
.\Upgrade-AzureWebApp.ps1 -ResourceGroup questionnaire-rg -WebApp questionnaire-prod

.EXAMPLE
.\Upgrade-AzureWebApp.ps1 -ResourceGroup questionnaire-rg -WebApp questionnaire-prod -Slot staging -Version 1.2.3

.EXAMPLE
.\Upgrade-AzureWebApp.ps1 -ResourceGroup questionnaire-rg -WebApp questionnaire-prod -Rollback .\backup\questionnaire-prod-20260916-120000.zip
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$WebApp,

    [ValidateNotNullOrEmpty()]
    [string]$Slot,

    [ValidatePattern('^v?\d+\.\d+\.\d+$')]
    [string]$Version,

    [ValidateNotNullOrEmpty()]
    [string]$BackupDirectory = '.\backup',

    [switch]$SkipMigration,

    [ValidateNotNullOrEmpty()]
    [string]$Rollback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    # **標準エラーを混ぜないこと。** az は警告を標準エラーへ書くので、
    # 2>&1 で混ぜると ConvertFrom-Json が壊れる
    $output = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }

    return $output | ConvertFrom-Json
}

function Invoke-AzCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & az @Arguments --only-show-errors --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
}

function Get-KuduAccessToken {
    $token = (& az account get-access-token --resource 'https://management.azure.com/' --query accessToken --output tsv 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw 'Unable to acquire an Azure access token for Kudu.'
    }

    return $token
}

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

function Invoke-KuduRequest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Get', 'Post', 'Put')]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Uri,

        [Parameter(Mandatory)]
        [string]$AccessToken,

        [string]$Body,

        [string]$InFile,

        [string]$OutFile
    )

    # Kudu REST API: https://github.com/projectkudu/kudu/wiki/REST-API
    # Azure App Service の Kudu 概要: https://learn.microsoft.com/azure/app-service/resources-kudu
    $headers = @{ Authorization = "Bearer $AccessToken" }
    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $headers
    }

    if ($PSBoundParameters.ContainsKey('Body')) {
        $parameters['Body'] = $Body
        $parameters['ContentType'] = 'application/json'
    }
    if ($PSBoundParameters.ContainsKey('InFile')) {
        $parameters['InFile'] = $InFile
        $parameters['ContentType'] = 'application/zip'
    }
    if ($PSBoundParameters.ContainsKey('OutFile')) {
        $parameters['OutFile'] = $OutFile
    }

    try {
        return Invoke-WebRequest @parameters
    }
    catch {
        $statusCode = Get-HttpStatusCode -ErrorRecord $_
        if ($statusCode -in 401, 403) {
            throw 'Kudu denied the request. Permission Microsoft.Web/sites/publish/Action is required for SCM access.'
        }

        throw
    }
}

function Write-KuduCommandResult {
    # **出力を必ず見せる。** どの移行で落ちたのかは、この出力にしか無い
    # （アプリの出力は英語。Issue #225）
    param(
        [Parameter(Mandatory)]
        [psobject]$Result
    )

    $outputProperty = $Result.PSObject.Properties['Output']
    if ($null -ne $outputProperty -and -not [string]::IsNullOrWhiteSpace($outputProperty.Value)) {
        Write-Host $outputProperty.Value.TrimEnd()
    }

    # **$error という名前を使わないこと。** PowerShell の自動変数を隠してしまう
    $errorProperty = $Result.PSObject.Properties['Error']
    if ($null -ne $errorProperty -and -not [string]::IsNullOrWhiteSpace($errorProperty.Value)) {
        Write-Host $errorProperty.Value.TrimEnd()
    }
}

function Test-HealthEndpoint {
    param(
        [Parameter(Mandatory)]
        [string]$Uri
    )

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Uri
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                Write-Host "Health check succeeded on attempt $attempt."
                return $response
            }
        }
        catch {
            if ($attempt -eq 12) {
                throw "Health check failed after $attempt attempts: $Uri"
            }
        }

        Write-Host "Health check attempt $attempt failed. Retrying in 10 seconds."
        Start-Sleep -Seconds 10
    }

    # **抜けたら失敗として扱う。** 2xx 以外を返し続けた場合に
    # 黙って戻ると、更新が成功したように見える
    throw "Health check failed after 12 attempts: $Uri"
}

if ($null -eq (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) was not found in PATH.'
}

$null = Invoke-AzJson -Arguments @('account', 'show')
$webAppArguments = @('webapp', 'show', '--resource-group', $ResourceGroup, '--name', $WebApp)
if ($Slot) {
    $webAppArguments += @('--slot', $Slot)
}
$webAppInfo = Invoke-AzJson -Arguments $webAppArguments

if ($Rollback) {
    $rollbackPath = [System.IO.Path]::GetFullPath($Rollback)
    if (-not (Test-Path -LiteralPath $rollbackPath -PathType Leaf)) {
        throw "Rollback backup was not found: $rollbackPath"
    }
    if ([System.IO.Path]::GetExtension($rollbackPath) -ne '.zip') {
        throw "Rollback backup must be a ZIP file: $rollbackPath"
    }
}

$deploymentArguments = @('--resource-group', $ResourceGroup, '--name', $WebApp)
if ($Slot) {
    $deploymentArguments += @('--slot', $Slot)
    $scmHost = "$WebApp-$Slot.scm.azurewebsites.net"
}
else {
    $scmHost = "$WebApp.scm.azurewebsites.net"
}

$healthUri = "https://$($webAppInfo.defaultHostName)/healthz"
$kuduBaseUri = "https://$scmHost"
$accessToken = Get-KuduAccessToken

if ($Rollback) {
    Write-Host "Restoring backup: $rollbackPath"
    if (-not $PSCmdlet.ShouldProcess("$WebApp site/wwwroot", "Restore backup $rollbackPath")) {
        Write-Host 'Rollback was not performed.'
        Write-Host 'Database was not rolled back. Restore it from the backup taken before migration if required.'
        return
    }

    # ⚠️ **PUT /api/zip は控えに無いファイルを消さない**（Kudu の REST API の仕様）。
    # 新版だけにあったファイルは残るので、運用者へ伝える
    Invoke-KuduRequest -Method Put -Uri "$kuduBaseUri/api/zip/site/wwwroot/" -AccessToken $accessToken -InFile $rollbackPath | Out-Null
    Invoke-AzCommand -Arguments (@('webapp', 'restart') + $deploymentArguments)
    Test-HealthEndpoint -Uri $healthUri | Out-Null
    Write-Host 'Rollback completed.'
    Write-Host 'Files that existed only in the newer release were not deleted. Remove them from site/wwwroot if required.'
    Write-Host 'Database was not rolled back. Restore it from the backup taken before migration if required.'
    return
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "QuestionnaireUpgrade-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    if ($Version) {
        $normalizedVersion = $Version.TrimStart('v')
    }
    else {
        $latestRelease = Invoke-RestMethod -Uri 'https://api.github.com/repos/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire/releases/latest'
        $normalizedVersion = ([string]$latestRelease.tag_name).TrimStart('v')
        if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
            throw "Latest Release tag is not a supported version: $($latestRelease.tag_name)"
        }
    }

    $assetName = "VehicleVision.PleasanterTools.Questionnaire-$normalizedVersion.zip"
    $zipPath = Join-Path $temporaryDirectory $assetName
    $checksumPath = "$zipPath.sha256"
    $releaseBaseUri = "https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Questionnaire/releases/download/v$normalizedVersion"

    Write-Host "Downloading release $normalizedVersion."
    Invoke-WebRequest -Uri "$releaseBaseUri/$assetName" -OutFile $zipPath
    Invoke-WebRequest -Uri "$releaseBaseUri/$assetName.sha256" -OutFile $checksumPath

    $expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]).ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 verification failed for $assetName."
    }
    Write-Host "SHA-256 verification succeeded for $assetName."

    if ($WhatIfPreference) {
        Write-Host 'WhatIf requested. No backup, deployment, migration, restart, or health check was performed.'
        return
    }

    if (-not $PSCmdlet.ShouldProcess("$WebApp site/wwwroot", "Back up, deploy release $normalizedVersion, migrate, and restart")) {
        Write-Host 'Update was not performed.'
        return
    }

    $resolvedBackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)
    New-Item -ItemType Directory -Path $resolvedBackupDirectory -Force | Out-Null
    $slotSuffix = if ($Slot) { "-$Slot" } else { '' }
    $backupTimestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $resolvedBackupDirectory "$WebApp$slotSuffix-$backupTimestamp.zip"
    $metadataPath = "$backupPath.metadata.json"

    # **更新前の /healthz は記録が目的。落ちていても更新を止めない。**
    # 起動に失敗した版を直すために更新することがある
    $currentStatusCode = $null
    $currentContent = $null
    try {
        $currentHealth = Invoke-WebRequest -Uri $healthUri
        $currentStatusCode = $currentHealth.StatusCode
        $currentContent = $currentHealth.Content
    }
    catch {
        Write-Host "The current /healthz did not respond. Continuing with the update."
    }

    [pscustomobject]@{
        RecordedAtUtc = [DateTime]::UtcNow.ToString('O')
        HealthUri = $healthUri
        HealthStatusCode = $currentStatusCode
        HealthResponse = $currentContent
    } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

    Write-Host "Creating wwwroot backup: $backupPath"
    Invoke-KuduRequest -Method Get -Uri "$kuduBaseUri/api/zip/site/wwwroot/" -AccessToken $accessToken -OutFile $backupPath | Out-Null
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf) -or (Get-Item -LiteralPath $backupPath).Length -eq 0) {
        throw "wwwroot backup was not created: $backupPath"
    }

    Write-Host "Deploying release $normalizedVersion."
    Invoke-AzCommand -Arguments (@('webapp', 'deploy', '--type', 'zip', '--src-path', $zipPath) + $deploymentArguments)

    if (-not $SkipMigration) {
        $migrationCommand = @{
            command = 'dotnet .\VehicleVision.PleasanterTools.Questionnaire.Web.dll --migrate --wait-for-db=180'
            dir = 'site\wwwroot'
        } | ConvertTo-Json -Compress
        $migrationResponse = Invoke-KuduRequest -Method Post -Uri "$kuduBaseUri/api/command" -AccessToken $accessToken -Body $migrationCommand
        $migrationResult = $migrationResponse.Content | ConvertFrom-Json
        Write-KuduCommandResult -Result $migrationResult
        if ($migrationResult.ExitCode -ne 0) {
            throw "Database migration failed with exit code $($migrationResult.ExitCode)."
        }

        $statusCommand = @{
            command = 'dotnet .\VehicleVision.PleasanterTools.Questionnaire.Web.dll --migrate-status'
            dir = 'site\wwwroot'
        } | ConvertTo-Json -Compress
        $statusResponse = Invoke-KuduRequest -Method Post -Uri "$kuduBaseUri/api/command" -AccessToken $accessToken -Body $statusCommand
        $statusResult = $statusResponse.Content | ConvertFrom-Json
        Write-KuduCommandResult -Result $statusResult
        if ($statusResult.ExitCode -ne 0) {
            throw "Migration status check failed with exit code $($statusResult.ExitCode)."
        }
    }
    else {
        Write-Host 'Database migration was skipped.'
    }

    Invoke-AzCommand -Arguments (@('webapp', 'restart') + $deploymentArguments)
    Test-HealthEndpoint -Uri $healthUri | Out-Null
    Write-Host 'Update completed.'
    Write-Host "Backup location: $resolvedBackupDirectory"
    Write-Host 'The backup ZIP can contain secrets. The operator is responsible for deleting it securely.'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
