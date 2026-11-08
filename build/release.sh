#!/usr/bin/env bash
# Cross-builds the Windows release on Linux/macOS (vpk "[win]" directive). Usage: build/release.sh 0.3.0 [https://github.com/you/koalix]
set -euo pipefail
VERSION="$1"; REPO="${2:-}"
cd "$(dirname "$0")/.."
command -v vpk >/dev/null || dotnet tool install -g vpk
[ -n "$REPO" ] && vpk "[win]" download github --repoUrl "$REPO" -o artifacts/releases ${GITHUB_TOKEN:+--token "$GITHUB_TOKEN"} || true
dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:Version="$VERSION" -o artifacts/velopack-input
vpk "[win]" pack -u KoalixApp -v "$VERSION" -r win-x64 -p artifacts/velopack-input -e KOALIX.exe \
  --packTitle KOALIX --packAuthors KOALIX -i installer/assets/koalix.ico -s installer/assets/splash.png -o artifacts/releases --yes
dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:Version="$VERSION" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/portable
if [ -n "$REPO" ]; then
  vpk "[win]" upload github --repoUrl "$REPO" --publish --releaseName "KOALIX $VERSION" --tag "v$VERSION" -o artifacts/releases ${GITHUB_TOKEN:+--token "$GITHUB_TOKEN"}
fi
