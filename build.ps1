# NX AI Assistant build + deploy script.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1            # build + copy to deploy/
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test       # also run unit tests
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Sign       # also sign with NX SignDotNet.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
#
# Notes:
# - Builds against NX2512 managed_core (.NET 8). NX provides NXOpen
#   assemblies at runtime; they are referenced with Private=false and never copied.
# - NX locks DLLs while loaded: close NX before redeploying.

param(
    [switch]$Test,
    [switch]$Sign,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$deployDir = Join-Path $repoRoot 'deploy\nxai_custom\application'
$startupDir = Join-Path $repoRoot 'deploy\nxai_custom\startup'
$menuSrc = Join-Path $repoRoot 'src\NXAI.Assistant.Entry\nxai_assistant.men'
$nxRoot = 'D:\Program Files\Siemens\DesigncenterNX2512'

Write-Host "== NX AI Assistant build ($Configuration) =="
dotnet build (Join-Path $repoRoot 'NXAIAssistant.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with code $LASTEXITCODE" }

if ($Test) {
    Write-Host '== unit tests =='
    dotnet test (Join-Path $repoRoot 'tests\NXAI.Assistant.Tests\NXAI.Assistant.Tests.csproj') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with code $LASTEXITCODE" }
}

Write-Host "== deploy to $deployDir =="
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
$payload = @(
    'src\NXAI.Assistant.Core\bin\Release\net8.0\NXAI.Assistant.Core.dll',
    'src\NXAI.Assistant.UI\bin\Release\net8.0-windows\NXAI.Assistant.UI.dll',
    'src\NXAI.Assistant.NxHost.NX2512\bin\Release\net8.0-windows\NXAI.Assistant.NxHost.NX2512.dll',
    'src\NXAI.Assistant.Entry\bin\Release\net8.0-windows\NXAI.Assistant.Entry.dll'
)
foreach ($rel in $payload) {
    $src = Join-Path $repoRoot ($rel -replace 'Release', $Configuration)
    Copy-Item -LiteralPath $src -Destination $deployDir -Force
    Write-Host "  copied: $(Split-Path -Leaf $rel)"
}

# MenuScript: main-menubar menus load from the custom directory's startup
# folder; action DLLs are resolved from its application folder.
New-Item -ItemType Directory -Force -Path $startupDir | Out-Null
Copy-Item -LiteralPath $menuSrc -Destination $startupDir -Force
Write-Host "  copied: nxai_assistant.men -> startup"

# A menu copy left in application\ would make NX load the same menu twice.
$staleMenu = Join-Path $deployDir 'nxai_assistant.men'
if (Test-Path -LiteralPath $staleMenu) {
    Remove-Item -LiteralPath $staleMenu -Force
    Write-Host "  removed stale menu from application\"
}

if ($Sign) {
    # SignDotNet requires the assembly to carry an "NX resource bundle"
    # (added by Siemens' NXOpen project wizard). This machine's NX2512 has been
    # observed loading unsigned .NET assemblies (legacy add-in evidence), so
    # signing is a fallback for environments that refuse unsigned DLLs.
    # See docs/TECHNICAL_SPIKE_RESULTS.md section 3 and docs/PHASE_0_REPORT.md.
    $signTool = Join-Path $nxRoot 'NXBIN\SignDotNet.exe'
    if (-not (Test-Path $signTool)) { throw "SignDotNet.exe not found: $signTool" }
    Write-Host '== signing with NX SignDotNet =='
    foreach ($rel in $payload) {
        $dll = Join-Path $deployDir (Split-Path -Leaf $rel)
        & $signTool $dll
        if ($LASTEXITCODE -ne 0) { throw "SignDotNet failed for $dll (does the assembly have the NX resource bundle?)" }
    }
}
else {
    Write-Host 'Sign step skipped. Run with -Sign if NX refuses to load unsigned assemblies (no dotnet_author license).'
}

Write-Host '== done =='
Write-Host "Deployment custom directory: $(Join-Path $repoRoot 'deploy\nxai_custom')"
Write-Host "Custom directories file:     $(Join-Path $repoRoot 'deploy\nxai_custom_dirs.dat')"
