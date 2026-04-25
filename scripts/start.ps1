[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

# -Clean implies -Build: clean+run with no build between them would leave
# stale or missing assemblies, which is never what you want.
if ($Clean) {
    dotnet clean ./Mithril.slnx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $Build = $true
}

if ($Build) {
    dotnet build ./Mithril.slnx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run ./Mithril.slnx --project ./src/Mithril.Shell/Mithril.Shell.csproj
