# DEPRECATED: Self-hosted Interfold deployments should use the Interfold.Bootstrapper binary
# (tools/Interfold.Bootstrapper) instead. The bootstrapper's SecretsPhase ports this script's
# logic to pure C# (System.Security.Cryptography). This script is retained for one release for
# compatibility with existing developer automation; it will be removed.
#
# Migration: run `dotnet run --project tools/Interfold.Bootstrapper -- bootstrap` from a Linux host.

param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "keys"),
    [int]$KeySize = 2048,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($KeySize -lt 2048) {
    throw "KeySize must be at least 2048."
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$privatePemPath = Join-Path $OutputDir "encryption-private.pem"
$publicPemPath = Join-Path $OutputDir "encryption-public.pem"
$privateBase64Path = Join-Path $OutputDir "encryption-private.base64.txt"
$envSnippetPath = Join-Path $OutputDir "encryption-env.txt"

if (-not $Force) {
    $existing = @(@($privatePemPath, $publicPemPath, $privateBase64Path, $envSnippetPath) | Where-Object { Test-Path $_ })
    if ($existing.Count -gt 0) {
        throw "Output file(s) already exist. Use -Force to overwrite.`n$($existing -join "`n")"
    }
}

$rsa = [System.Security.Cryptography.RSA]::Create($KeySize)
try {
    $privatePem = $rsa.ExportRSAPrivateKeyPem()
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
}
finally {
    $rsa.Dispose()
}

[System.IO.File]::WriteAllText($privatePemPath, $privatePem, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($publicPemPath, $publicPem, [System.Text.UTF8Encoding]::new($false))

$privateBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($privatePem))
[System.IO.File]::WriteAllText($privateBase64Path, $privateBase64, [System.Text.UTF8Encoding]::new($false))

$envSnippet = @(
    "# Set this in your secret store or environment"
    "ENCRYPTION_PRIVATE_KEY=$privateBase64"
)
[System.IO.File]::WriteAllLines($envSnippetPath, $envSnippet, [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated encryption keypair:" -ForegroundColor Green
Write-Host "  Private PEM:  $privatePemPath"
Write-Host "  Public PEM:   $publicPemPath"
Write-Host "  Private b64:  $privateBase64Path"
Write-Host "  Env snippet:  $envSnippetPath"
Write-Host ""
Write-Host "Use the base64 value for ENCRYPTION_PRIVATE_KEY." -ForegroundColor Yellow
