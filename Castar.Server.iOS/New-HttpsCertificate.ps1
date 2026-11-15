#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Net.IPAddress]$Address,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1024, 65535)][int]$Port = 5443
)

$ErrorActionPreference = 'Stop'
if ($Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
    [Net.IPAddress]::IsLoopback($Address) -or $Address.Equals([Net.IPAddress]::Any)) {
    throw 'Supply the iPhone IPv4 address on the local network.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Choose a new output directory.'
}
$directory = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=Castar iPhone $Address", $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddIpAddress($Address)
    $request.CertificateExtensions.Add($san.Build())
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $keyUsage = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsage, $true))
    $eku = [Security.Cryptography.OidCollection]::new()
    [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))

    $now = [DateTimeOffset]::UtcNow
    $certificate = $request.CreateSelfSigned($now.AddMinutes(-10), $now.AddDays(365))
    try {
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        [IO.File]::WriteAllBytes((Join-Path $directory 'https.pfx'),
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $password))
        [IO.File]::WriteAllBytes((Join-Path $directory 'castar-iphone.cer'),
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $endpoint = 'https://{0}:{1}' -f $Address, $Port
        $environment = @(
            "Castar__ClientAddress=$endpoint",
            'Kestrel__Certificates__Default__Path=/var/jb/etc/castar/https.pfx',
            "Kestrel__Certificates__Default__Password=$password",
            ''
        )
        [IO.File]::WriteAllText((Join-Path $directory 'https.env'), $environment -join "`n")
        Write-Output $endpoint
    }
    finally {
        $certificate.Dispose()
    }
}
finally {
    $rsa.Dispose()
}
