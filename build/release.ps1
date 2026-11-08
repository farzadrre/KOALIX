# KOALIX release script (Windows / PowerShell 7). Produces:
#   artifacts/releases/KoalixApp-win-Setup.exe   (installer with in-app updates)
#   artifacts/releases/*.nupkg + releases.win.json (update feed — upload these)
#   artifacts/portable/KOALIX.exe                (single-file portable build)
param(
  [Parameter(Mandatory)] [string] $Version,          # e.g. 0.3.0
  [string] $GithubRepo = "",                         # e.g. https://github.com/you/koalix  (optional: publish feed)
  [string] $GithubToken = $env:GITHUB_TOKEN
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet tool update -g vpk | Out-Null

# Download the previous release so vpk can build a small delta package.
if ($GithubRepo) { vpk download github --repoUrl $GithubRepo -o artifacts/releases --token $GithubToken }

dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o artifacts/velopack-input
vpk pack -u KoalixApp -v $Version -r win-x64 -p artifacts/velopack-input -e KOALIX.exe `
  --packTitle KOALIX --packAuthors KOALIX -i installer/assets/koalix.ico -s installer/assets/splash.png -o artifacts/releases

dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/portable

if ($GithubRepo) {
  vpk upload github --repoUrl $GithubRepo --publish --releaseName "KOALIX $Version" --tag "v$Version" -o artifacts/releases --token $GithubToken
  Write-Host "Published. In KOALIX › Settings › Updates set the source to $GithubRepo"
}
