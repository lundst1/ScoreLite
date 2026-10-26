#!/usr/bin/env bash
# Hämtar GeneralUser GS (K14, DR-05) till en given mapp och kontrollerar SHA-256. Binärfilen checkas inte in i repot.
# Användning: tools/fetch-soundfont.sh <målmapp>   (t.ex. src/ScoreLite.App/Resources/Raw)
set -euo pipefail
dest="${1:?målmapp saknas}"
expected="9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe"
mkdir -p "$dest"
curl -fsSL -o "$dest/soundfont.sf2" "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/main/GeneralUser-GS.sf2"
actual=$(sha256sum "$dest/soundfont.sf2" | cut -d' ' -f1)
if [ "$actual" != "$expected" ]; then echo "SHA-256 stämmer inte: $actual" >&2; rm -f "$dest/soundfont.sf2"; exit 1; fi
echo "OK: $dest/soundfont.sf2 (GeneralUser GS v2.0.3, $(stat -c %s "$dest/soundfont.sf2") byte)"
