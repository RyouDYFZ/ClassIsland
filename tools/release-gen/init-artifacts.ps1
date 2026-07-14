$ErrorActionPreference = "Stop"

# Generate metadata & upload artifacts

if ($(Test-Path ./out) -eq $false) {
    mkdir out
}

$artifacts = Get-ChildItem -Path ./out_artifacts -Directory

foreach ($artifact in $artifacts) {
    if ($artifact.Name.Contains("out_app_") -ne $true) {
        continue
    }
    Copy-Item ./out_artifacts/$($artifact.Name)/* -Destination ./out/ -Recurse -Force
}

foreach ($artifact in $(Get-ChildItem ./out)) {
    Move-Item $artifact.FullName -Destination $artifact.FullName.Replace("out_app_", "ClassIsland_app_") -Force
}

# Artifact file names are normalized for public distribution. Regenerate sidecar
# hashes afterwards so each checksum references the renamed file by basename.
foreach ($checksum in $(Get-ChildItem ./out -File -Filter "*.sha256")) {
    $payloadPath = $checksum.FullName.Substring(0, $checksum.FullName.Length - ".sha256".Length)
    if ($(Test-Path -LiteralPath $payloadPath -PathType Leaf) -eq $false) {
        throw "Checksum payload not found: $payloadPath"
    }

    $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadName = [System.IO.Path]::GetFileName($payloadPath)
    Set-Content -LiteralPath $checksum.FullName -Value "$hash  $payloadName" -Encoding ascii
}
