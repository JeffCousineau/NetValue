$ErrorActionPreference = 'Stop'
$sdkExecutable = Join-Path $env:TEMP 'netvalue-dotnet-10\dotnet.exe'
if (!(Test-Path -LiteralPath $sdkExecutable)) {
    $sdkExecutable = (Get-Command dotnet -ErrorAction Stop).Source
}
$projectDirectory = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectDirectory
try {
    $credential = Read-Host 'Paste the Entra client secret VALUE (input is hidden)' -AsSecureString
    $secretValue = [System.Net.NetworkCredential]::new('', $credential).Password
    if ([string]::IsNullOrWhiteSpace($secretValue)) { throw 'A client secret value is required.' }
    @{ 'Authentication:ClientSecret' = $secretValue } | ConvertTo-Json | & $sdkExecutable user-secrets set
    if ($LASTEXITCODE -ne 0) { throw 'Could not save the secret. Check that the .NET SDK is installed.' }
    Write-Output 'Local authentication secret saved. Restart NetValue with dotnet run.'
} finally {
    $secretValue = $null
    $credential = $null
    Pop-Location
}
