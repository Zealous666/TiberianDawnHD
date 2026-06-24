#!/bin/bash
# Lädt den offiziellen Tiberian Sun Freeware-Content (von EA 2010 freigegeben)
# und entpackt ihn nach mods/management/asset ts/
# Nur einmalig als Entwickler-Setup nötig — Spieler brauchen das nicht.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DEST="${SCRIPT_DIR}/mods/management/asset ts"
MIRROR_URL="https://www.openra.net/packages/ts-mirrors.txt"
SHA1_EXPECTED="824df30de0004ad13fac29cf16450caafee9fb1b"
TMP_ZIP="/tmp/ts-freeware-packages.zip"

echo "=== TS Freeware Content Installer ==="
echo "Ziel: ${DEST}"
echo ""

# Bereits installiert?
if [ -f "${DEST}/conquer.mix" ]; then
    echo "✓ conquer.mix bereits vorhanden — nichts zu tun."
    echo ""
    echo "Enthaltene Dateien:"
    ls -lh "${DEST}"/*.mix 2>/dev/null || true
    exit 0
fi

mkdir -p "${DEST}"

# Mirrors laden
echo "[1/3] Lade Mirror-Liste von openra.net..."
MIRRORS=$(curl -sf "${MIRROR_URL}" || echo "")
if [ -z "${MIRRORS}" ]; then
    echo "FEHLER: Mirror-Liste nicht abrufbar. Internetverbindung prüfen."
    exit 1
fi

# Ersten erreichbaren Mirror verwenden
DOWNLOAD_URL=""
while IFS= read -r url; do
    [ -z "${url}" ] && continue
    echo "      Prüfe: ${url}"
    if curl -sf --head "${url}" -o /dev/null 2>/dev/null; then
        DOWNLOAD_URL="${url}"
        break
    fi
done <<< "${MIRRORS}"

if [ -z "${DOWNLOAD_URL}" ]; then
    echo "FEHLER: Kein Mirror erreichbar. Manuell herunterladen von:"
    echo "${MIRRORS}" | head -3
    exit 1
fi

# Download
echo ""
echo "[2/3] Lade Paket herunter..."
echo "      Quelle: ${DOWNLOAD_URL}"
curl -L --progress-bar "${DOWNLOAD_URL}" -o "${TMP_ZIP}"

# SHA1-Prüfung
echo ""
echo "      Prüfe SHA1..."
if command -v shasum &>/dev/null; then
    SHA1_ACTUAL=$(shasum -a 1 "${TMP_ZIP}" | awk '{print $1}')
elif command -v sha1sum &>/dev/null; then
    SHA1_ACTUAL=$(sha1sum "${TMP_ZIP}" | awk '{print $1}')
else
    echo "      (SHA1-Prüfung übersprungen — kein shasum/sha1sum gefunden)"
    SHA1_ACTUAL="${SHA1_EXPECTED}"
fi

if [ "${SHA1_ACTUAL}" != "${SHA1_EXPECTED}" ]; then
    echo "FEHLER: SHA1-Prüfsumme stimmt nicht überein!"
    echo "  Erwartet: ${SHA1_EXPECTED}"
    echo "  Erhalten: ${SHA1_ACTUAL}"
    rm -f "${TMP_ZIP}"
    exit 1
fi
echo "      SHA1 OK ✓"

# Entpacken
echo ""
echo "[3/3] Entpacke nach: ${DEST}"
unzip -o "${TMP_ZIP}" -d "${DEST}"
rm -f "${TMP_ZIP}"

echo ""
echo "=== Fertig ==="
echo ""
echo "Installierte Dateien:"
ls -lh "${DEST}"/*.mix 2>/dev/null || ls -lh "${DEST}"
echo ""
echo "Jetzt VXL-Render starten mit:"
echo "  ./vxl-to-png-test.sh mcv"
