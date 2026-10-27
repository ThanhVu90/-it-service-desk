param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/ServiceDesk/ServiceDesk.csproj'
if (-not $env:Demo__Password) {
    $secret = Read-Host 'Choose a local demo password (at least 12 characters; reused for the four demo accounts)' -AsSecureString
    $env:Demo__Password = [System.Net.NetworkCredential]::new('', $secret).Password
}
if ($env:Demo__Password.Length -lt 12) { throw 'Password must contain at least 12 characters.' }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Demo__Seed = 'true'
Write-Host "Open http://127.0.0.1:$Port and sign in as admin@example.test."
Write-Host 'Existing database accounts retain their original password.'
dotnet run --project $project --no-launch-profile --urls "http://127.0.0.1:$Port"
if ($LASTEXITCODE -ne 0) { throw "Application exited with code $LASTEXITCODE" }
