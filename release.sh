#!/usr/bin/env bash
# Builds the exe, tags with its FileVersion and publishes the controller
# Release (single-file exe asset).
# To bump the version, change AssemblyVersion in FreebuffController.cs and run
# this script again.
#
# Usage: bash release.sh          # requires an authenticated gh CLI
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="${REPO:-Dima-Sor/freebuff-controller}"
# Notes for the Release page come from this file's `## <tag>` section.
CHANGELOG_FILE="${CHANGELOG_FILE:-CHANGELOG-RU.md}"
WINHERE="$(cygpath -w "$HERE")"

cd "$HERE"
# The session-handover script is embedded into the exe as Base64, so refresh it
# before compiling (python3 first, Windows often only has python — fallback).
python3 tools/embed-handover.py 2>/dev/null || python tools/embed-handover.py || \
  { echo "EMBED FAILED (needs python3)" >&2; exit 1; }
# Build the single-file exe directly with csc. Avoid `cmd //c build.bat`,
# which git-bash can't invoke when a sandbox blocks cmd.
CSC="${SYSTEMROOT:-C:\Windows}/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
"$CSC" -nologo -target:winexe -platform:anycpu -optimize+ -codepage:65001 \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Management.dll \
  -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  -win32icon:"app.ico" -out:"FreebuffController.exe" "FreebuffController.cs" \
  || { echo "BUILD FAILED (csc error, see above)" >&2; exit 1; }
echo "built FreebuffController.exe"

VER="$(powershell -NoProfile -Command "(Get-Item '${WINHERE}\\FreebuffController.exe').VersionInfo.FileVersion" | tr -d '\r' | sed 's/\.[0-9]*$//')"
TAG="${TAG:-v${VER}-ru}"
echo "FreebuffController.exe v${VER} → Release ${TAG}"

# SHA512 digest alongside the exe (sha512sum-style "<hex>  <file>" lines).
# OnSelfUpdateClick fetches this to verify the download before swapping the
# running exe; without it the self-update silently skips verification.
if command -v sha512sum >/dev/null 2>&1; then
  sha512sum FreebuffController.exe > sha512.txt
else
  HEX="$(powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA512 '${WINHERE}\\FreebuffController.exe').Hash.ToLower()" | tr -d '\r')"
  printf '%s  FreebuffController.exe\n' "$HEX" > sha512.txt
fi

if gh release view "${TAG}" -R "${REPO}" >/dev/null 2>&1; then
  echo "ERROR: Release ${TAG} already exists. Bump AssemblyVersion in FreebuffController.cs" >&2
  echo "  for a new build, or run gh release delete ${TAG} -R ${REPO} --yes first." >&2
  exit 1
fi

# Release notes come from this release's section in the changelog (## vX.Y.Z up
# to the next ##). If the section is missing, bail out: the Release page is for
# people, so a missing line is better than an empty or stale one.
NOTES="$(awk -v ver="${TAG}" '
  $0 ~ ("^## " ver "([ ·]|$)") { found = 1; next }
  found && /^## / { exit }
  found { print }
' "${CHANGELOG_FILE}")"
if [ -z "$(printf '%s' "${NOTES}" | tr -d '[:space:]')" ]; then
  echo "ERROR: no ${TAG} section in ${CHANGELOG_FILE}, cannot build release notes." >&2
  echo "  Add a section on top (what changed + what it means for the user) titled" >&2
  echo "  «## ${TAG} · $(date +%F)» and run this again." >&2
  exit 1
fi

gh release create "${TAG}" "${HERE}/FreebuffController.exe" "${HERE}/sha512.txt" -R "${REPO}" \
  --title "Freebuff Controller v${VER} (RU)" \
  --notes "${NOTES}"
echo "Published ${TAG}."
