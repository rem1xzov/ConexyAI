#!/usr/bin/env sh
# Обёртка для CI (Linux/macOS): нужен pwsh 7+.
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec pwsh -NoProfile -File "$here/publish.ps1" "$@"
