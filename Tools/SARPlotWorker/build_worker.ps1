param(
    [string]$PythonExecutable = ""
)

$ErrorActionPreference = "Stop"
$env:PYTHONDONTWRITEBYTECODE = "1"
$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $toolRoot "../..")).Path
$source = Join-Path $projectRoot "Assets/StreamingAssets/SARAnalytics/SensitivityAnalysis.py"
$trafficSource = Join-Path $projectRoot "Assets/StreamingAssets/SARAnalytics/TrafficHeatmapAnalysis.py"
$runtimeRequirements = Join-Path $projectRoot "Assets/StreamingAssets/SARAnalytics/requirements.txt"
$buildRequirements = Join-Path $toolRoot "requirements-build.txt"
$licenseCollector = Join-Path $toolRoot "collect_licenses.py"
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $PythonExecutable = Join-Path $projectRoot "Output/Visualization/.venv/Scripts/python.exe"
}
if (-not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) {
    throw "Python executable not found: $PythonExecutable"
}
foreach ($workerSource in @($source, $trafficSource)) {
    if (-not (Test-Path -LiteralPath $workerSource -PathType Leaf)) {
        throw "Worker source not found: $workerSource"
    }
}
foreach ($requiredFile in @($runtimeRequirements, $buildRequirements, $licenseCollector)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Worker build input is missing: $requiredFile"
    }
}
$pythonBase = (& $PythonExecutable -c "import sys; print(sys.base_prefix)").Trim()
$condaLibraryBin = Join-Path $pythonBase "Library/bin"
$requiredCondaDlls = @("ffi.dll", "libbz2.dll", "libcrypto-3-x64.dll", "libssl-3-x64.dll")
foreach ($dll in $requiredCondaDlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $condaLibraryBin $dll) -PathType Leaf)) {
        throw "Required Python runtime DLL is missing: $(Join-Path $condaLibraryBin $dll)"
    }
}

$buildRoot = Join-Path $toolRoot "build"
$distRoot = Join-Path $toolRoot "dist"
foreach ($target in @($buildRoot, $distRoot)) {
    $resolvedParent = (Resolve-Path -LiteralPath $toolRoot).Path.TrimEnd('\') + '\'
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected worker path: $fullTarget"
    }
    if (Test-Path -LiteralPath $fullTarget) {
        Remove-Item -LiteralPath $fullTarget -Recurse -Force
    }
}

& $PythonExecutable -m PyInstaller `
    --noconfirm `
    --clean `
    --onedir `
    --name SarSensitivityRenderer `
    --hidden-import matplotlib.backends.backend_agg `
    --hidden-import TrafficHeatmapAnalysis `
    --exclude-module tkinter `
    --exclude-module PyQt5 `
    --exclude-module PyQt6 `
    --exclude-module PySide2 `
    --exclude-module PySide6 `
    --add-binary "$(Join-Path $condaLibraryBin 'ffi.dll');." `
    --add-binary "$(Join-Path $condaLibraryBin 'libbz2.dll');." `
    --add-binary "$(Join-Path $condaLibraryBin 'libcrypto-3-x64.dll');." `
    --add-binary "$(Join-Path $condaLibraryBin 'libssl-3-x64.dll');." `
    --distpath $distRoot `
    --workpath $buildRoot `
    --specpath $toolRoot `
    $source
if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller failed with exit code $LASTEXITCODE"
}

$workerDirectory = Join-Path $distRoot "SarSensitivityRenderer"
$workerExecutable = Join-Path $workerDirectory "SarSensitivityRenderer.exe"
if (-not (Test-Path -LiteralPath $workerExecutable -PathType Leaf)) {
    throw "PyInstaller output is missing: $workerExecutable"
}

$smokeRoot = Join-Path $toolRoot "smoke"
if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$smokeRequest = Join-Path $smokeRoot "request.json"
$smokeOutput = Join-Path $smokeRoot "output.png"
$smokeResponse = Join-Path $smokeRoot "response.json"
[ordered]@{
    schema_version = 1
    kind = "tested-grid-empirical-response"
    title = "Worker smoke test"
    selected_factors = @("x")
    selected_metrics = @("metric", "delay")
    all_varying_factors = @("x")
    runs = @(
        [ordered]@{ run_id = "a"; duration = 10; parameters = @{ x = "0" }; metrics = @{ metric = 1; delay = 10 } },
        [ordered]@{ run_id = "b"; duration = 10; parameters = @{ x = "1" }; metrics = @{ metric = 2; delay = 20 } }
    )
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $smokeRequest -Encoding utf8
& $workerExecutable --request $smokeRequest --output $smokeOutput --response $smokeResponse
if ($LASTEXITCODE -ne 0 -or
    -not (Test-Path -LiteralPath $smokeOutput -PathType Leaf) -or
    -not (Test-Path -LiteralPath $smokeResponse -PathType Leaf) -or
    -not ((Get-Content -LiteralPath $smokeResponse -Raw | ConvertFrom-Json).success)) {
    throw "Packaged plotting worker failed its headless smoke test."
}

$comparisonRequest = Join-Path $smokeRoot "comparison.request.json"
$comparisonOutput = Join-Path $smokeRoot "comparison.png"
$comparisonResponse = Join-Path $smokeRoot "comparison.response.json"
[ordered]@{
    schema_version = 1
    kind = "run-metric-bar-comparison"
    title = "Worker comparison smoke test"
    selected_metrics = @("metric", "delay")
    runs = @(
        [ordered]@{ run_id = "a"; label = "Batch 1/2 - a"; duration = 10; parameters = @{ x = "0" }; metrics = @{ metric = 1; delay = 10 } },
        [ordered]@{ run_id = "b"; label = "Run 2 - b"; duration = 10; parameters = @{ x = "1" }; metrics = @{ metric = 2; delay = 20 } }
    )
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $comparisonRequest -Encoding utf8
& $workerExecutable --request $comparisonRequest --output $comparisonOutput --response $comparisonResponse
if ($LASTEXITCODE -ne 0 -or
    -not (Test-Path -LiteralPath $comparisonOutput -PathType Leaf) -or
    -not (Test-Path -LiteralPath $comparisonResponse -PathType Leaf) -or
    -not ((Get-Content -LiteralPath $comparisonResponse -Raw | ConvertFrom-Json).success)) {
    throw "Packaged plotting worker failed its multi-metric run-comparison smoke test."
}

$trafficRequest = Join-Path $smokeRoot "traffic.request.json"
$trafficOutput = Join-Path $smokeRoot "traffic.png"
$trafficResponse = Join-Path $smokeRoot "traffic.response.json"
[ordered]@{
    schema_version = 1
    kind = "traffic-heatmap"
    title = "Worker traffic-heatmap smoke test"
    visible_layer_ids = @("L0")
    network = [ordered]@{
        AltitudeLayers = @(
            [ordered]@{ LayerId = "L0"; DisplayName = "Lower"; NominalAltitude = 75; Enabled = $true },
            [ordered]@{ LayerId = "L1"; DisplayName = "Upper"; NominalAltitude = 125; Enabled = $true }
        )
        Edges = @(
            [ordered]@{ EdgeId = "E0"; LayerId = "L0"; Lanes = @(
                [ordered]@{ LaneId = "L0-0"; LayerId = "L0"; Centerline = @(
                    [ordered]@{ x = 0; y = 75; z = 0 },
                    [ordered]@{ x = 100; y = 75; z = 0 }
                ) }
            ) },
            [ordered]@{ EdgeId = "E1"; LayerId = "L1"; Lanes = @(
                [ordered]@{ LaneId = "L1-0"; LayerId = "L1"; Centerline = @(
                    [ordered]@{ x = 0; y = 125; z = 20 },
                    [ordered]@{ x = 100; y = 125; z = 20 }
                ) }
            ) }
        )
        LaneConnectionCorridors = @(
            [ordered]@{ CorridorId = "must-not-be-rendered"; Centerline = @(
                [ordered]@{ x = 100; y = 75; z = 0 },
                [ordered]@{ x = 0; y = 125; z = 20 }
            ) }
        )
    }
    vertiports = @(
        [ordered]@{
            VertiportId = "V0"
            ApproachNet = [ordered]@{
                Nodes = @(
                    [ordered]@{ NodeId = "A0"; Position = [ordered]@{ x = -20; y = 0; z = 0 } },
                    [ordered]@{ NodeId = "A1"; Position = [ordered]@{ x = 0; y = 0; z = 0 } }
                )
                Edges = @(
                    [ordered]@{ EdgeId = "AE0"; FromNodeId = "A0"; ToNodeId = "A1" }
                )
            }
            HoldingStructures = @(
                [ordered]@{
                    Vertices = @(
                        [ordered]@{ VertexId = "HV0"; Position = [ordered]@{ x = 0; y = 0; z = 0 } },
                        [ordered]@{ VertexId = "HV1"; Position = [ordered]@{ x = 12; y = 0; z = 8 } }
                    )
                    Circles = @()
                    Ramps = @(
                        [ordered]@{ RampId = "R0"; FromVertexId = "HV0"; ToVertexId = "HV1"; LinkCircleId = "" }
                    )
                }
            )
            Corridors = @(
                [ordered]@{
                    CorridorId = "C0"; CorridorType = "Exit"; LinkLaneId = "L0-0"; LinkRampId = "R0"
                    LaneLinkPoint = [ordered]@{ x = 40; y = 75; z = 0 }
                    RampLinkPoint = [ordered]@{ x = 12; y = 0; z = 8 }
                }
            )
        }
    )
    lane_traversals = @{ "L0-0" = 8; "L1-0" = 99 }
    resource_reservation_seconds = @{ "AE0" = 4.0; "R0" = 8.5; "C0" = 12.5 }
} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $trafficRequest -Encoding utf8
& $workerExecutable --request $trafficRequest --output $trafficOutput --response $trafficResponse
if ($LASTEXITCODE -ne 0 -or
    -not (Test-Path -LiteralPath $trafficOutput -PathType Leaf) -or
    -not (Test-Path -LiteralPath $trafficResponse -PathType Leaf) -or
    -not ((Get-Content -LiteralPath $trafficResponse -Raw | ConvertFrom-Json).success)) {
    throw "Packaged plotting worker failed its traffic-heatmap smoke test."
}
Remove-Item -LiteralPath $smokeRoot -Recurse -Force

$packageRoot = Join-Path $projectRoot "Assets/StreamingAssets/SARAnalytics/Worker"
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$licenseRoot = Join-Path $packageRoot "THIRD_PARTY_LICENSES"
& $PythonExecutable $licenseCollector `
    --output $licenseRoot `
    --runtime-requirements $runtimeRequirements `
    --build-requirements $buildRequirements `
    --worker-directory $workerDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Third-party license collection failed with exit code $LASTEXITCODE"
}
$componentsPath = Join-Path $licenseRoot "COMPONENTS.json"
if (-not (Test-Path -LiteralPath $componentsPath -PathType Leaf)) {
    throw "Third-party component inventory is missing: $componentsPath"
}
$packagedBuildRequirements = Join-Path (Split-Path -Parent $packageRoot) "build-requirements.txt"
Copy-Item -LiteralPath $buildRequirements -Destination $packagedBuildRequirements -Force
$archiveName = "SarSensitivityRenderer-win-x64-v1.zip"
$archivePath = Join-Path $packageRoot $archiveName
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -LiteralPath $workerDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$trafficSourceHash = (Get-FileHash -LiteralPath $trafficSource -Algorithm SHA256).Hash.ToLowerInvariant()
$componentsHash = (Get-FileHash -LiteralPath $componentsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeRequirementsHash = (Get-FileHash -LiteralPath $runtimeRequirements -Algorithm SHA256).Hash.ToLowerInvariant()
$buildRequirementsHash = (Get-FileHash -LiteralPath $packagedBuildRequirements -Algorithm SHA256).Hash.ToLowerInvariant()
$pythonVersion = (& $PythonExecutable -c "import platform; print(platform.python_version())").Trim()
$pyInstallerVersion = (& $PythonExecutable -c "import PyInstaller; print(PyInstaller.__version__)").Trim()
$manifest = [ordered]@{
    SchemaVersion = 1
    Version = "1.1.0"
    ArchiveFile = $archiveName
    Sha256 = $hash
    SourceSha256 = $sourceHash
    TrafficHeatmapSourceSha256 = $trafficSourceHash
    ExecutableRelativePath = "SarSensitivityRenderer/SarSensitivityRenderer.exe"
    PythonVersion = $pythonVersion
    PyInstallerVersion = $pyInstallerVersion
    RuntimeRequirementsSha256 = $runtimeRequirementsHash
    BuildRequirementsSha256 = $buildRequirementsHash
    ThirdPartyComponentsRelativePath = "THIRD_PARTY_LICENSES/COMPONENTS.json"
    ThirdPartyComponentsSha256 = $componentsHash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot "manifest.json") -Encoding utf8

Write-Host "SAR plotting worker packaged: $archivePath"
Write-Host "SHA-256: $hash"
Write-Host "Third-party inventory: $componentsPath"
