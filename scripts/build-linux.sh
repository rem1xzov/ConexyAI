#!/usr/bin/env bash
# Сборка релиза на Linux/macOS без PowerShell.
# Требуется .NET 9 SDK. Для подписи манифеста — ключ ECDSA.
#
#   CONEXY_VERSION=0.9.29 CONEXY_SIGNING_KEY=/root/.update-signing-key.pem \
#     DOTNET=/root/.dotnet/dotnet bash scripts/build-linux.sh
#
# Переменные: CONEXY_VERSION, CONEXY_RIDS, CONEXY_OUTPUT, CONEXY_URL_BASE,
#             CONEXY_SIGNING_KEY, DOTNET.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-dotnet}"
VERSION="${CONEXY_VERSION:-0.9.29}"
OUT="${CONEXY_OUTPUT:-dist}"
RIDS="${CONEXY_RIDS:-win-x64 linux-x64 osx-x64 osx-arm64}"
KEY="${CONEXY_SIGNING_KEY:-}"
URL_BASE="${CONEXY_URL_BASE:-https://conexyai.ru/downloads}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

rm -rf "$OUT"
mkdir -p "$OUT"

for rid in $RIDS; do
  echo "=== $rid ==="
  "$DOTNET" publish ConexyAI.Desktop/ConexyAI.Desktop.csproj \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=None -p:DebugSymbols=false \
    -p:Version="$VERSION" -p:FileVersion="$VERSION.0" \
    -o "$OUT/$rid" --nologo

  if [ -f "$OUT/$rid/conexy.exe" ]; then
    cp "$OUT/$rid/conexy.exe" "$OUT/conexy-$rid.exe"
  elif [ -f "$OUT/$rid/conexy" ]; then
    cp "$OUT/$rid/conexy" "$OUT/conexy-$rid"
    chmod +x "$OUT/conexy-$rid"
  else
    echo "Не найден собранный клиент в $OUT/$rid" >&2
    exit 1
  fi
  rm -rf "$OUT/$rid"
done

if [ -n "$KEY" ]; then
  echo "=== manifest ==="
  "$DOTNET" run --project ConexyAI.Desktop/ConexyAI.Desktop.csproj -c Release -- \
    updatesign "$OUT" "$VERSION" --url-base "$URL_BASE" --key "$KEY"
  "$DOTNET" run --project ConexyAI.Desktop/ConexyAI.Desktop.csproj -c Release -- updateverify "$OUT"
else
  echo "Ключ подписи не задан — latest.json не создаётся."
fi

echo "=== files ==="
ls -lh "$OUT"
