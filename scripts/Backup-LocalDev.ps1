#Requires -Version 7.0

# **Write-Host は意図して使っている。** 手元で進み具合を追うための出力であり、
# パイプラインへ渡すデータではない
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingWriteHost', '', Justification = '進み具合を見せるため')]

<#
.SYNOPSIS
手元の検証環境（Docker）の控えを作り、書き戻します。

.DESCRIPTION
本アプリの DB、Pleasanter の DB、App_Data/Parameters の手元だけの設定をまとめて控えます。

写しの一式や結合テストは検証環境を `down -v` で作り直すことを前提にしているため、
作ったアンケートや管理者は毎回消えます。作り直す前にこれで控えておくと戻せます。

⚠️ **検証環境専用です。本番の DB へ向けないでください。**
接続先は compose.yaml の検証環境に固定しており、資格情報も検証環境用の固定値です。

.PARAMETER Provider
控える本アプリの DB です。SqlServer（既定）、PostgreSql、MySql から選びます。

.PARAMETER Restore
書き戻す控えのフォルダです。指定すると控えを作らず書き戻します。

.PARAMETER OutputRoot
控えの置き場所です。既定はリポジトリ直下の backups です。

.PARAMETER SkipPleasanter
Pleasanter の DB を対象から外します。

.EXAMPLE
./scripts/Backup-LocalDev.ps1
SQL Server の検証環境と Pleasanter を控えます。

.EXAMPLE
./scripts/Backup-LocalDev.ps1 -Restore backups/20260917-153000
その控えを書き戻します。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('SqlServer', 'PostgreSql', 'MySql')]
    [string]$Provider = 'SqlServer',

    [string]$Restore,

    [string]$OutputRoot,

    [switch]$SkipPleasanter
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# **検証環境用の固定値。** compose.yaml の既定と同じにする。**本番の値ではない**
$password = if ($env:TESTENV_SA_PASSWORD) { $env:TESTENV_SA_PASSWORD } else { 'Questionnaire#Test1' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot

# **compose は必ずリポジトリ直下で実行する。** 別の場所だと別のプロジェクトを掴む
function Get-ServiceContainerId {
    param([Parameter(Mandatory)][string]$Service)

    Push-Location $repositoryRoot
    try {
        $id = (& docker compose ps -q $Service) | Select-Object -First 1
    }
    finally {
        Pop-Location
    }

    if (-not $id) {
        throw "The '$Service' container is not running. Start it first: docker compose --profile sqlserver up -d --wait"
    }

    return $id
}

function Restart-Pleasanter {
    Push-Location $repositoryRoot
    try {
        & docker compose restart pleasanter | Out-Null
    }
    finally {
        Pop-Location
    }
}

# 本アプリの DB ごとの、控えの置き場所
$databases = @{
    SqlServer  = @{ Service = 'db'; FileName = 'questionnaire-sqlserver.bak' }
    PostgreSql = @{ Service = 'db-postgres'; FileName = 'questionnaire-postgres.sql' }
    MySql      = @{ Service = 'db-mysql'; FileName = 'questionnaire-mysql.sql' }
}

function Backup-AppDatabase {
    param([Parameter(Mandatory)][string]$Destination)

    $spec = $databases[$Provider]
    $containerId = Get-ServiceContainerId -Service $spec.Service
    $target = Join-Path $Destination $spec.FileName

    switch ($Provider) {
        'SqlServer' {
            # **コンテナの中で BACKUP してから取り出す。** 書き出し先は中にしか作れない
            $inside = '/var/opt/mssql/data/questionnaire-backup.bak'
            & docker exec $containerId /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q "BACKUP DATABASE [Questionnaire] TO DISK = N'$inside' WITH INIT, FORMAT" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'BACKUP DATABASE failed.' }
            & docker cp "${containerId}:$inside" $target | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'docker cp failed.' }
        }
        'PostgreSql' {
            & docker exec $containerId pg_dump -U postgres -d questionnaire --clean --if-exists | Set-Content -LiteralPath $target -Encoding utf8NoBOM
            if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }
        }
        'MySql' {
            & docker exec $containerId mysqldump -uroot "-p$password" --databases questionnaire | Set-Content -LiteralPath $target -Encoding utf8NoBOM
            if ($LASTEXITCODE -ne 0) { throw 'mysqldump failed.' }
        }
    }

    Write-Host "  saved $($spec.FileName)"
}

function Restore-AppDatabase {
    param([Parameter(Mandatory)][string]$Source)

    $spec = $databases[$Provider]
    $target = Join-Path $Source $spec.FileName
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "  skipped $($spec.FileName) (not in this backup)"
        return
    }

    $containerId = Get-ServiceContainerId -Service $spec.Service

    switch ($Provider) {
        'SqlServer' {
            $inside = '/var/opt/mssql/data/questionnaire-restore.bak'
            & docker cp $target "${containerId}:$inside" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'docker cp failed.' }

            # **接続が残っていると戻せない。** 単独利用者へ落としてから戻す
            $sql = "ALTER DATABASE [Questionnaire] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [Questionnaire] FROM DISK = N'$inside' WITH REPLACE; ALTER DATABASE [Questionnaire] SET MULTI_USER;"
            & docker exec $containerId /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q $sql | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'RESTORE DATABASE failed.' }
        }
        'PostgreSql' {
            Get-Content -LiteralPath $target -Raw | & docker exec -i $containerId psql -U postgres -d questionnaire | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'psql failed.' }
        }
        'MySql' {
            Get-Content -LiteralPath $target -Raw | & docker exec -i $containerId mysql -uroot "-p$password" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'mysql failed.' }
        }
    }

    Write-Host "  restored $($spec.FileName)"
}

function Backup-PleasanterDatabase {
    param([Parameter(Mandatory)][string]$Destination)

    # **Pleasanter も同じ SQL Server に載っている**（compose.yaml の db）
    $containerId = Get-ServiceContainerId -Service 'db'
    $inside = '/var/opt/mssql/data/pleasanter-backup.bak'
    & docker exec $containerId /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q "BACKUP DATABASE [Implem.Pleasanter] TO DISK = N'$inside' WITH INIT, FORMAT" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'BACKUP DATABASE (Pleasanter) failed.' }
    & docker cp "${containerId}:$inside" (Join-Path $Destination 'pleasanter.bak') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker cp failed.' }
    Write-Host '  saved pleasanter.bak'
}

function Restore-PleasanterDatabase {
    param([Parameter(Mandatory)][string]$Source)

    $target = Join-Path $Source 'pleasanter.bak'
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host '  skipped pleasanter.bak (not in this backup)'
        return
    }

    $containerId = Get-ServiceContainerId -Service 'db'
    $inside = '/var/opt/mssql/data/pleasanter-restore.bak'
    & docker cp $target "${containerId}:$inside" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker cp failed.' }

    $sql = "ALTER DATABASE [Implem.Pleasanter] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [Implem.Pleasanter] FROM DISK = N'$inside' WITH REPLACE; ALTER DATABASE [Implem.Pleasanter] SET MULTI_USER;"
    & docker exec $containerId /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q $sql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'RESTORE DATABASE (Pleasanter) failed.' }

    # ⚠️ **利用者情報がキャッシュされている。** 戻したら起こし直す
    # （tools/pleasanter-testenv/README.md の「キーを変えたら再起動」と同じ理由）
    Restart-Pleasanter
    Write-Host '  restored pleasanter.bak'
}

# App_Data/Parameters の *.local.json（手元だけの設定。**git の管理外**）
$parametersPath = Join-Path $repositoryRoot 'App_Data/Parameters'

function Backup-LocalParameter {
    param([Parameter(Mandatory)][string]$Destination)

    $files = @(Get-ChildItem -LiteralPath $parametersPath -Filter '*.local.json' -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        Write-Host '  no *.local.json to save'
        return
    }

    $into = Join-Path $Destination 'Parameters'
    New-Item -ItemType Directory -Path $into -Force | Out-Null
    $files | Copy-Item -Destination $into
    Write-Host "  saved $($files.Count) local parameter file(s)"
}

function Restore-LocalParameter {
    param([Parameter(Mandatory)][string]$Source)

    $from = Join-Path $Source 'Parameters'
    if (-not (Test-Path -LiteralPath $from)) {
        Write-Host '  no local parameter files in this backup'
        return
    }

    Get-ChildItem -LiteralPath $from -Filter '*.local.json' | Copy-Item -Destination $parametersPath -Force
    Write-Host '  restored local parameter files'
}

if ($Restore) {
    $source = (Resolve-Path -LiteralPath $Restore).Path
    Write-Host "Restoring the local test environment from $source"
    Write-Host '  *** This overwrites the local databases. Never point it at production. ***'

    if ($PSCmdlet.ShouldProcess($source, 'Restore the local test environment')) {
        Restore-AppDatabase -Source $source
        if (-not $SkipPleasanter) { Restore-PleasanterDatabase -Source $source }
        Restore-LocalParameter -Source $source
        Write-Host 'Done.'
    }

    return
}

$root = if ($OutputRoot) { $OutputRoot } else { Join-Path $repositoryRoot 'backups' }
$destination = Join-Path $root (Get-Date -Format 'yyyyMMdd-HHmmss')

Write-Host "Backing up the local test environment ($Provider) to $destination"
if ($PSCmdlet.ShouldProcess($destination, 'Back up the local test environment')) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Backup-AppDatabase -Destination $destination
    if (-not $SkipPleasanter) { Backup-PleasanterDatabase -Destination $destination }
    Backup-LocalParameter -Destination $destination
    Write-Host "Done. Restore with: ./scripts/Backup-LocalDev.ps1 -Restore $destination"
}
