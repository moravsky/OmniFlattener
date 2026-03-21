param(
    [ValidateSet("Debug", "Release", IgnoreCase = $true)]
    [string]$Config = "Debug",

    [ValidateSet("Dev", "Prod", IgnoreCase = $true)]
    [string]$Target = ""
)

# Default Target based on Config if not explicitly provided
if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = if ($Config -eq "Release") { "Prod" } else { "Dev" }
}

# Normalize casing
$Config = (Get-Culture).TextInfo.ToTitleCase($Config.ToLower())
$Target = (Get-Culture).TextInfo.ToTitleCase($Target.ToLower())

# Guard: warn if deploying Debug to Prod
if ($Config -eq "Debug" -and $Target -eq "Prod") {
    Write-Warning "Deploying a Debug build to Prod. Press Ctrl+C to abort."
    Start-Sleep -Seconds 5
}

$root   = $PSScriptRoot
$source = Join-Path $root "OmniFlattenerStrategy\bin\$Config"

$destRoot = if ($Target -eq "Dev") { "C:\QuantowerDev" } else { "C:\Quantower" }
$dest     = "$destRoot\Settings\Scripts\Strategies\OmniFlattener"

# Safety checks
if ([string]::IsNullOrWhiteSpace($dest)) {
    Write-Error "Destination path is empty. Aborting."
    exit 1
}

if ($dest -notmatch "Quantower.*OmniFlattener$") {
    Write-Error "Destination path doesn't look right: $dest. Aborting."
    exit 1
}

if (-not (Test-Path $source)) {
    Write-Error "Source path doesn't exist: $source. Did you build first?"
    exit 1
}

# Clean destination
if (Test-Path $dest) {
    Write-Host "Cleaning $dest"
    Remove-Item "$dest\*" -Force -Recurse
} else {
    Write-Host "Destination does not exist, creating: $dest"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
}

# Copy new files
$files = @("*.dll", "*.pdb", "*.deps.json")

foreach ($pattern in $files) {
    $items = Get-ChildItem -Path $source -Filter $pattern
    foreach ($item in $items) {
        Copy-Item $item.FullName -Destination $dest -Force
        Write-Host "Deployed: $($item.Name)"
    }
}

Write-Host "[$Config -> $Target] Deploy complete to $dest"
