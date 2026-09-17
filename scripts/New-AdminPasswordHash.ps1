#Requires -Version 7.0

<#
.SYNOPSIS
Generates a password hash for the AdminUsers table.

.DESCRIPTION
Produces the stored form used by PasswordHasher (PBKDF2-HMACSHA512, 210000 iterations,
16-byte salt, 32-byte key), printed as "v1$<iterations>$<salt>$<key>" in Base64.

Use this only to recover from a lockout where no administrator can sign in.
See _documents/管理者の棚卸し-運用手順書.md.

.PARAMETER Password
The new password, as a SecureString. Omit to be prompted without echoing the value.

.EXAMPLE
.\New-AdminPasswordHash.ps1

.EXAMPLE
# Non-interactive. Read the password from a secret store, never from a literal.
.\New-AdminPasswordHash.ps1 -Password (Get-Secret 'questionnaire-admin')

.EXAMPLE
# Last resort for a one-off run. The literal stays in shell history.
.\New-AdminPasswordHash.ps1 -Password (ConvertTo-SecureString 'P@ssw0rd!' -AsPlainText -Force)
#>
[CmdletBinding()]
param(
    [securestring]$Password
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# **引数で平文を受け取らない。** シェルの履歴と操作ログへ残る
if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'New password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
if ([string]::IsNullOrEmpty($plain)) {
    throw 'Password must not be empty.'
}

# ⚠️ **この 4 つを PasswordHasher.cs と食い違わせないこと。**
# 食い違うと、出来上がった値では二度とログインできない
$iterations = 210000
$saltBytes = 16
$keyBytes = 32

$salt = [byte[]]::new($saltBytes)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($salt)

# KeyDerivation.Pbkdf2(HMACSHA512) と同じ値になる
$kdf = [System.Security.Cryptography.Rfc2898DeriveBytes]::new(
    $plain, $salt, $iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA512)
try {
    $key = $kdf.GetBytes($keyBytes)
}
finally {
    $kdf.Dispose()
    # **平文を持ち回らない**
    $plain = $null
}

Write-Output ('v1${0}${1}${2}' -f $iterations, [Convert]::ToBase64String($salt), [Convert]::ToBase64String($key))
