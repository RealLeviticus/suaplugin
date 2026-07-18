<#
.SYNOPSIS
Generates cloudflare-pages/public/areas.geojson from a vatSys profile's
RestrictedAreas.xml so the website map can draw every Danger/Restricted area.

Geometry is static (it only changes with an AIRAC/profile update), so it is
baked into a static asset served by Cloudflare Pages rather than uploaded on
every plugin sync. Re-run this whenever the profile's RestrictedAreas.xml
changes, then redeploy Pages.
#>
param(
    [string]$RestrictedAreasXml = (Join-Path $env:USERPROFILE "Documents\vatSys Files\Profiles\Australia\RestrictedAreas.xml"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "cloudflare-pages\public\areas.geojson")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RestrictedAreasXml)) {
    throw "RestrictedAreas.xml not found at '$RestrictedAreasXml'. Pass -RestrictedAreasXml with the correct profile path."
}

# One coordinate token is "<lat><lon>", e.g. "-214700.000+1140930.000":
#   lat = sign + DDMMSS.sss   (2-digit degrees, 6 integer digits)
#   lon = sign + DDDMMSS.sss  (3-digit degrees, 7 integer digits)
$tokenRegex = [regex]'^([+-]\d{6}(?:\.\d+)?)([+-]\d{7}(?:\.\d+)?)$'

function ConvertTo-Decimal {
    param([string]$Dms, [int]$DegreeDigits)
    $sign = if ($Dms[0] -eq '-') { -1 } else { 1 }
    $body = $Dms.Substring(1)
    $deg = [double]$body.Substring(0, $DegreeDigits)
    $min = [double]$body.Substring($DegreeDigits, 2)
    $sec = [double]$body.Substring($DegreeDigits + 2)
    return $sign * ($deg + $min / 60.0 + $sec / 3600.0)
}

[xml]$doc = Get-Content -LiteralPath $RestrictedAreasXml -Raw
$features = New-Object System.Collections.Generic.List[object]
$skipped = 0

foreach ($area in $doc.RestrictedAreas.Areas.RestrictedArea) {
    $name = [string]$area.Name
    $body = [string]$area.Area
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($body)) { $skipped++; continue }

    $ring = New-Object System.Collections.Generic.List[object]
    foreach ($tok in ($body -split '/')) {
        $t = $tok.Trim()
        if ($t.Length -eq 0) { continue }
        $m = $tokenRegex.Match($t)
        if (-not $m.Success) { continue }
        $lat = ConvertTo-Decimal -Dms $m.Groups[1].Value -DegreeDigits 2
        $lon = ConvertTo-Decimal -Dms $m.Groups[2].Value -DegreeDigits 3
        # GeoJSON is [longitude, latitude]; round to ~1 m to keep the file small.
        $ring.Add(@([math]::Round($lon, 5), [math]::Round($lat, 5)))
    }

    if ($ring.Count -lt 3) { $skipped++; continue }
    # GeoJSON linear rings must be closed.
    $first = $ring[0]; $last = $ring[$ring.Count - 1]
    if ($first[0] -ne $last[0] -or $first[1] -ne $last[1]) { $ring.Add(@($first[0], $first[1])) }

    $floor = 0; [void][int]::TryParse([string]$area.AltitudeFloor, [ref]$floor)
    $ceiling = 0; [void][int]::TryParse([string]$area.AltitudeCeiling, [ref]$ceiling)

    $features.Add([ordered]@{
        type       = "Feature"
        properties = [ordered]@{
            name    = $name
            type    = [string]$area.Type
            floor   = $floor
            ceiling = $ceiling
            # Dataset default draw style; the map falls back to this when an area
            # has no live activated line pattern.
            line    = [string]$area.LinePattern
        }
        geometry   = [ordered]@{
            type        = "Polygon"
            coordinates = @(, $ring.ToArray())
        }
    })
}

$collection = [ordered]@{
    type     = "FeatureCollection"
    features = $features.ToArray()
}

New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
# Depth must clear FeatureCollection > Feature > geometry > coordinates > ring > point.
$json = $collection | ConvertTo-Json -Depth 8 -Compress
Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8

$sizeKb = [math]::Round((Get-Item -LiteralPath $OutputPath).Length / 1KB, 1)
Write-Host "Wrote $($features.Count) areas ($skipped skipped) to '$OutputPath' ($sizeKb KB)." -ForegroundColor Green
