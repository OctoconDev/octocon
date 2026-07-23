# DEPRECATED: Self-hosted Interfold deployments should use the Interfold.Bootstrapper binary
# (tools/Interfold.Bootstrapper) instead. The bootstrapper's CertificatePhase ports this script's
# logic to pure C# (System.Security.Cryptography). This script is retained for one release for
# compatibility with existing developer automation; it will be removed.
#
# Migration: run `dotnet run --project tools/Interfold.Bootstrapper -- bootstrap --config interfold.bootstrap.json`
# from a Linux host.

param(
    [string]$RootName = 'Interfold SS Root CA',
    [string]$Domains = 'api.octocon.dev,api.octocon.app',
    [string]$OutputPath = '.\certs',
    [Parameter(Mandatory=$true)][string]$Password,
    [int]$Years = 8
)

$SubjectArray = $Domains -split ","
$NotAfter = (Get-Date).AddYears($Years)

$Certificate = @{
    Extension =[System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 0, $true)
    Subject =  "CN=$RootName"
    NotAfter = $NotAfter
    KeyUsage = "CertSign"
}

$Root = New-SelfSignedCertificate @Certificate
$Cert = New-SelfSignedCertificate -DnsName $SubjectArray -Signer $Root -NotAfter $NotAfter -KeyUsage DigitalSignature, KeyEncipherment

Write-Host "Certificates created"

# Move root to store
$MyCertPath = Join-Path Cert:\LocalMachine\My $Root.Thumbprint
Move-Item $MyCertPath -Destination Cert:\LocalMachine\Root

if (-not (Test-Path $OutputPath -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputPath
}

# Export root as .cer
$RootCertPath = Join-Path Cert:\LocalMachine\Root $Root.Thumbprint
$MovedRoot = Get-ChildItem -Path $RootCertPath
Export-Certificate -Cert $MovedRoot -FilePath (Join-Path $OutputPath "$RootName.cer")

# Export leaf as .pfx
$mypwd = ConvertTo-SecureString -String $Password -Force -AsPlainText
$LeafCertPath = Join-Path Cert:\LocalMachine\My $Cert.Thumbprint
Export-PfxCertificate -Cert $LeafCertPath -FilePath (Join-Path $OutputPath "$RootName.pfx") -Password $mypwd

Write-Host "Certificates exported."