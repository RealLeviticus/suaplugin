<#
.SYNOPSIS
Creates one private machine sync token, stores it in Cloudflare secrets, and
writes the installed plugin's untracked config without printing the token.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$AccountId,
    [string]$PluginDirectory = (Join-Path $env:USERPROFILE "Documents\vatSys Files\Profiles\Australia\Plugins\SuaAirspacePlugin")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDirectoryPath = [IO.Path]::GetFullPath($PluginDirectory)
if (-not (Test-Path -LiteralPath $pluginDirectoryPath -PathType Container)) {
    throw "Plugin directory not found: $pluginDirectoryPath"
}

$configPath = Join-Path $pluginDirectoryPath "SuaAirspacePlugin.config.json"
$syncToken = ""
if (Test-Path -LiteralPath $configPath) {
    try { $syncToken = [string]((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).SyncToken) }
    catch { $syncToken = "" }
}
if ([string]::IsNullOrWhiteSpace($syncToken)) {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $syncToken = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$env:CLOUDFLARE_ACCOUNT_ID = $AccountId
$syncToken | & wrangler pages secret put SUA_SYNC_TOKEN --project-name sua-airspace
if ($LASTEXITCODE -ne 0) { throw "Failed to store the Pages sync secret." }
$syncToken | & wrangler secret put SUA_SYNC_TOKEN --config (Join-Path $root "cloudflare-automation\wrangler.toml")
if ($LASTEXITCODE -ne 0) { throw "Failed to store the automation Worker sync secret." }

$config = [ordered]@{
    PublicUiUrl = "https://sua.actuallyleviticus.xyz/"
    CloudApiUrl = "https://sua-airspace.pages.dev/"
    SyncToken = $syncToken
    SyncIntervalSeconds = 5
}
$json = $config | ConvertTo-Json
[IO.File]::WriteAllText($configPath, $json, [Text.UTF8Encoding]::new($false))

Write-Host "Cloud sync secrets and the private installed-plugin config are configured." -ForegroundColor Green
