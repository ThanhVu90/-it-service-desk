# SQLite backup/restore without copying a live WAL file.
# The maintenance command uses the SQLite backup API through the application binary,
# so the snapshot is page-consistent even while the server is running.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Out,
    [string]$AppPath = (Join-Path $PSScriptRoot '../src/ServiceDesk/bin/Release/net10.0/ServiceDesk.dll'),
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../src/ServiceDesk/ServiceDesk.csproj'),
    [switch]$Restore,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $AppPath) -or $Build) {
    Write-Host 'Building the application first (Release)...'
    dotnet build (Join-Path $PSScriptRoot '../ServiceDesk.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if (-not (Test-Path $Source)) { throw "Source database not found: $Source" }
if (-not $Out) {
    $kind = if ($Restore) { 'restore' } else { 'backup' }
    $Out = Join-Path (Split-Path -Parent (Resolve-Path $Source)) "backups/$kind-desk-$(Get-Date -Format 'yyyyMMdd-HHmmss').db"
    New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
}
$action = if ($Restore) { 'db-restore' } else { 'db-backup' }

Write-Host "$action -> $Out"
dotnet $AppPath $action --source $Source --out $Out
if ($LASTEXITCODE -ne 0) { throw "$action failed with exit code $LASTEXITCODE." }

if ($Restore) {
    Write-Host 'Start the application against the restored copy with:'
    Write-Host "  `"ConnectionStrings__Desk`" = `'Data Source=$Out;Foreign Keys=True`'"
}
else {
    Write-Host 'Snapshot is immediately runnable: restore it elsewhere with the db-restore command, never edit it in place.'
}
