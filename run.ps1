param([string]$Repository)

$ErrorActionPreference = 'Stop'
$taskProjectPath = Join-Path $PSScriptRoot 'src\GitManager.App\GitManager.App.csproj'
if ($Repository) {
    dotnet run --project $taskProjectPath -- --repository $Repository
}
else {
    dotnet run --project $taskProjectPath
}
exit $LASTEXITCODE
