$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet restore
dotnet publish .\PrivacyScreen.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o .\publish
Write-Host "已发布：$PSScriptRoot\publish\PrivacyScreen.exe"
