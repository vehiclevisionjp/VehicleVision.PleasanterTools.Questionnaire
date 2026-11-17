#Requires -Version 7.0

<#
.SYNOPSIS
Upgrade-AzureWebApp.ps1 の -WhatIf を、az と Invoke-WebRequest を差し替えて検証します（Issue #1004）。

.DESCRIPTION
本物の Azure・GitHub へは接続しません。Pester は使いません（CI の実行環境へ追加の導入を要さないため）。
失敗した検証があれば、終了コード 1 で終わります。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..' 'Upgrade-AzureWebApp.ps1'
$failures = [System.Collections.Generic.List[string]]::new()
$global:AzCalls = @()
$global:WebCalls = @()
$global:BadHash = $false

# スクリプトは呼び出し元の関数を動的スコープで解決するので、同名の関数で差し替えられる
function az {
    $global:LASTEXITCODE = 0
    $global:AzCalls += , ($args -join ' ')
    if ($args -contains 'get-access-token') {
        return 'dummy-token'
    }
    if ($args -contains 'webapp' -and $args -contains 'show') {
        return '{"defaultHostName":"example.azurewebsites.net"}'
    }
    return '{}'
}

function Invoke-WebRequest {
    param($Uri, $OutFile, $Method, $Headers, $Body, $InFile, $ContentType)
    $global:WebCalls += , "$Uri"
    if ($OutFile) {
        # 取得先のディレクトリが無ければ、本物と同じく失敗させる
        $directory = Split-Path -Parent $OutFile
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "Could not find a part of the path '$OutFile'."
        }
        if ($Uri.EndsWith('.sha256')) {
            $zip = $OutFile.Substring(0, $OutFile.Length - '.sha256'.Length)
            $hash = if ($global:BadHash) { '0' * 64 } else { (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash }
            Set-Content -LiteralPath $OutFile -Value "$hash  zip" -WhatIf:$false
        }
        else {
            Set-Content -LiteralPath $OutFile -Value 'dummy zip' -WhatIf:$false
        }
    }
}

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "ok: $Message"
    }
    else {
        Write-Host "FAILED: $Message"
        $failures.Add($Message)
    }
}

function Get-TemporaryDirectories {
    @(Get-ChildItem -LiteralPath ([System.IO.Path]::GetTempPath()) -Filter 'QuestionnaireUpgrade-*' -Directory)
}

# --- 正常系: 取得と照合まで進み、配置以降は行わない ---
$before = @(Get-TemporaryDirectories).Count
$output = & $scriptPath -ResourceGroup rg -WebApp app -Version 1.2.3 -WhatIf 6>&1 | Out-String

Assert-That ($output -match 'SHA-256 verification succeeded') '-WhatIf reaches SHA-256 verification'
Assert-That ($output -match 'WhatIf requested') '-WhatIf reports that nothing after verification is performed'
Assert-That (@($global:WebCalls | Where-Object { $_ -like '*releases/download/v1.2.3/*' }).Count -eq 2) 'downloads the ZIP and the .sha256 file'
Assert-That (@($global:AzCalls | Where-Object { $_ -match 'deploy|restart|appsettings|config' }).Count -eq 0) 'does not deploy, restart, or change settings through az'
Assert-That (@($global:WebCalls | Where-Object { $_ -match 'scm\.azurewebsites\.net|/healthz|/ready' }).Count -eq 0) 'sends no request to Kudu or the health endpoints'
Assert-That (@(Get-TemporaryDirectories).Count -eq $before) 'leaves no temporary directory after -WhatIf'

# --- 異常系: 照合に失敗したら -WhatIf でも失敗する ---
$global:BadHash = $true
$message = $null
try {
    & $scriptPath -ResourceGroup rg -WebApp app -Version 1.2.3 -WhatIf 6>&1 | Out-Null
}
catch {
    $message = $_.Exception.Message
}
Assert-That ($message -like '*SHA-256 verification failed*') 'fails on a SHA-256 mismatch even with -WhatIf'
Assert-That (@(Get-TemporaryDirectories).Count -eq $before) 'leaves no temporary directory after a failure'

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
