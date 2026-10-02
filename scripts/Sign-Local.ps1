param(
    [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\OnDemandPerf')
)
$ErrorActionPreference = 'Stop'

# Development signatures only: Smart App Control requires a publicly trusted signer.
$files = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object {
    $_.Name -like 'OnDemandPerf*.exe' -or $_.Name -like 'OnDemandPerf*.dll'
})
if ($files.Count -eq 0) { throw "No OnDemandPerf binaries found in $Path. Run scripts/Build.ps1 first." }

$subject = 'CN=OnDemandPerf Local Development'
$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object {
    $_.Subject -eq $subject -and $_.Issuer -eq $subject -and $_.HasPrivateKey -and
    $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date).AddDays(30)
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -FriendlyName 'OnDemandPerf local development signing' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1)
}

foreach ($file in $files) {
    $signature = Set-AuthenticodeSignature -LiteralPath $file.FullName -Certificate $certificate -HashAlgorithm SHA256
    $verified = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($verified.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
        $verified.Status -in @('NotSigned', 'HashMismatch', 'NotSupportedFileFormat')) {
        throw "Signing failed for $($file.FullName): $($signature.StatusMessage)"
    }
    [pscustomobject]@{ File = $file.FullName; Status = $verified.Status; Message = $verified.StatusMessage }
}
Write-Host "Signing certificate: $($certificate.Thumbprint)"
Write-Warning 'These are self-signed development signatures, without a timestamp. The certificate is not installed in a trust store. This does not resolve Smart App Control blocks.'
