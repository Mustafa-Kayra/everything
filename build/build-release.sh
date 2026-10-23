#!/usr/bin/env bash
# Portable exe + kurulum paketi üretir: artifacts/Everything-Portable.exe, artifacts/Everything-Setup.exe, artifacts/SHA256SUMS.txt
# Linux'ta: .NET SDK ve Wine + Inno Setup gerekir (INNO_ISCC ile ISCC.exe yolu verilebilir).
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(grep -oP '(?<=<Version>)[^<]+' src/Everything.App/Everything.App.csproj)
iscc=${INNO_ISCC:-"$HOME/.wine-everything/drive_c/InnoSetup/ISCC.exe"}
export WINEPREFIX=${WINEPREFIX:-$HOME/.wine-everything} WINEDEBUG=-all

rm -rf artifacts
dotnet test tests/Everything.Core.Tests -c Release
dotnet publish src/Everything.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o artifacts/publish

cp artifacts/publish/EverythingArama.exe artifacts/Everything-Portable.exe
wine "$iscc" /Q "/DAppVersion=$version" installer/EverythingArama.iss

(cd artifacts && sha256sum Everything-Portable.exe Everything-Setup.exe > SHA256SUMS.txt && cat SHA256SUMS.txt)
echo "Sürüm $version hazır: artifacts/"
