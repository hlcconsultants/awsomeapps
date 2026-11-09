# Runs the tests, publishes a clean self-contained build, and compiles the installer.
param([string]$Version = "1.0.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "publish"

function Invoke-Step([scriptblock]$Step) {
    & $Step
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $Step" }
}

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found." }

Invoke-Step { dotnet test "$root\tests\FastGet.Tests" -c Release -nologo -v q }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force -Confirm:$false }
foreach ($project in "FastGet.App", "FastGet.Host") {
    Invoke-Step { dotnet publish "$root\src\$project" -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version -nologo -v q }
}

# The self-contained build redistributes the .NET runtime, so its notices ship with it.
$dotnetRoot = Split-Path (Get-Command dotnet).Source
Copy-Item "$dotnetRoot\LICENSE.txt" "$publish\dotnet-LICENSE.txt"
Copy-Item "$dotnetRoot\ThirdPartyNotices.txt" "$publish\dotnet-ThirdPartyNotices.txt"

$output = Join-Path $PSScriptRoot "output"
if (Test-Path $output) { Remove-Item $output -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $output | Out-Null

Invoke-Step { & $iscc /Q "/DAppVersion=$Version" "$PSScriptRoot\FastGet.iss" }

# The package attached to releases keeps its "key", so its ID matches the app when loaded unpacked.
# The Chrome Web Store package drops it because the store rejects a "key" on a first upload.
$packageDir = Join-Path ([IO.Path]::GetTempPath()) "fastget-extension-$([guid]::NewGuid().ToString('N'))"
Copy-Item "$root\extension" $packageDir -Recurse
try {
    $manifest = Get-Content "$packageDir\manifest.json" -Raw | ConvertFrom-Json
    $manifest.version = $Version
    $manifest | ConvertTo-Json -Depth 10 | Set-Content "$packageDir\manifest.json"
    Compress-Archive -Path "$packageDir\*" -DestinationPath "$output\FastGet-Extension-$Version.zip"

    $manifest.PSObject.Properties.Remove("key")
    $manifest | ConvertTo-Json -Depth 10 | Set-Content "$packageDir\manifest.json"
    Compress-Archive -Path "$packageDir\*" -DestinationPath "$output\FastGet-Extension-Store-$Version.zip"
}
finally {
    Remove-Item $packageDir -Recurse -Force -Confirm:$false
}

Get-ChildItem $output | Select-Object FullName, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
