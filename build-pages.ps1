<#
.SYNOPSIS
Generates the Cloudflare Pages static site from the plugin's embedded UI source.
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "cloudflare-pages\public")
)

$ErrorActionPreference = "Stop"
$sourcePath = Join-Path $PSScriptRoot "SuaAirspacePlugin\SuaUiPage.cs"
$source = Get-Content -LiteralPath $sourcePath -Raw
$match = [regex]::Match($source, 'public const string Html = @"(?s)(.*)";\s*\}\s*$')
if (-not $match.Success) {
    throw "Could not extract SuaUiPage.Html from '$sourcePath'."
}

$html = $match.Groups[1].Value.Replace('""', '"')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $OutputDirectory "index.html") -Value $html -Encoding UTF8
Write-Host "Generated Cloudflare Pages UI at '$OutputDirectory'." -ForegroundColor Green
