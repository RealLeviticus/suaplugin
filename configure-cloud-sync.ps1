<#
.SYNOPSIS
Creates one private automation refresh token and stores it as the SUA_SYNC_TOKEN
secret for the Cloudflare Pages project and the automation Worker without
printing it. The plugin does not use this token; it only protects the Worker's
manual /refresh endpoint.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$AccountId
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$syncToken = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

$env:CLOUDFLARE_ACCOUNT_ID = $AccountId
$syncToken | & wrangler pages secret put SUA_SYNC_TOKEN --project-name sua-airspace
if ($LASTEXITCODE -ne 0) { throw "Failed to store the Pages sync secret." }
$syncToken | & wrangler secret put SUA_SYNC_TOKEN --config (Join-Path $root "cloudflare-automation\wrangler.toml")
if ($LASTEXITCODE -ne 0) { throw "Failed to store the automation Worker sync secret." }

Write-Host "SUA_SYNC_TOKEN is configured for the Pages project and automation Worker." -ForegroundColor Green
