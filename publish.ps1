$ErrorActionPreference = 'Stop'
$taskProjectPath = Join-Path $PSScriptRoot 'src\GitManager.App\GitManager.App.csproj'
$taskPublishPath = Join-Path $PSScriptRoot 'artifacts\app'
dotnet publish $taskProjectPath --configuration Release --output $taskPublishPath --self-contained false --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output (Join-Path $taskPublishPath 'GitManager.exe')
