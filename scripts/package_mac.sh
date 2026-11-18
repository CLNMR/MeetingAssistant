#!/usr/bin/env bash
# Packs build/MeetingAssistant.app into a DMG for a release:
#   VERSION=1.2.3 ./scripts/build_app.sh && VERSION=1.2.3 ./scripts/package_mac.sh
# With a "Developer ID Application" identity (SIGN_IDENTITY) and notary credentials
# (NOTARY_APPLE_ID, NOTARY_PASSWORD = app-specific password, NOTARY_TEAM_ID) the DMG is
# signed, notarized and stapled. Without them it stays ad-hoc signed (see README › Install).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
APP="build/MeetingAssistant.app"
VERSION="${VERSION:-dev}"
DMG="build/MeetingAssistant-$VERSION-mac-arm64.dmg"
[[ -d "$APP" ]] || { echo "Run scripts/build_app.sh first"; exit 1; }

echo "==> Creating $DMG"
STAGE="$(mktemp -d)"; trap 'command rm -rf "$STAGE"' EXIT
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
command rm -f "$DMG"
hdiutil create -volname "MeetingAssistant $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null

if [[ -n "${NOTARY_APPLE_ID:-}" && -n "${SIGN_IDENTITY:-}" && "$SIGN_IDENTITY" == Developer\ ID* ]]; then
  echo "==> Signing and notarizing"
  codesign --force -s "$SIGN_IDENTITY" "$DMG"
  xcrun notarytool submit "$DMG" --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_PASSWORD" \
    --team-id "$NOTARY_TEAM_ID" --wait
  xcrun stapler staple "$DMG"
else
  echo "==> Not notarized (no Developer ID identity and notary credentials)"
fi
echo "Done: $ROOT/$DMG"
