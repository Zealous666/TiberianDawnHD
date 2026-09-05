#!/bin/bash
# VXL → PNG Test-Script
# Baut den Utility Command neu, extrahiert TS-Assets und rendert eine Test-Einheit.
#
# Voraussetzung: Tiberian Sun muss installiert sein (via OpenRA ts-content Installer,
# Origin/EA App C&C Collection, oder manuelle Installation).
# Erwarteter Content-Pfad: ~/Library/Application Support/OpenRA/Content/ts/
#
# Verwendung:
#   ./vxl-to-png-test.sh [UNIT]
#   UNIT = VXL-Basisname ohne Extension, z.B. "mcv" (default), "hmec", "bggy"
#
# Ausgabe: mods/management/vxl-render/<unit>/<unit>-0000.png .. <unit>-0031.png

set -e

UNIT="${1:-mcv}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENGINE_DIR="${SCRIPT_DIR}/engine"
CONTENT_DIR="${SCRIPT_DIR}/mods/management/asset ts"
OUT_DIR="${SCRIPT_DIR}/mods/management/vxl-render/${UNIT}"

echo "=== VXL → PNG Renderer ==="
echo "Einheit : ${UNIT}"
echo "Content : ${CONTENT_DIR}"
echo "Ausgabe : ${OUT_DIR}"
echo ""

# ── Schritt 1: Engine neu kompilieren ──────────────────────────────────────────
echo "[1/4] Kompiliere OpenRA.Mods.Cnc..."
cd "${ENGINE_DIR}"
dotnet build OpenRA.Mods.Cnc/OpenRA.Mods.Cnc.csproj \
    -c Release \
    --no-dependencies \
    -o bin \
    --nologo -v quiet
echo "      OK — bin/OpenRA.Mods.Cnc.dll aktualisiert"
cd "${SCRIPT_DIR}"

# ── Schritt 2: TS Content prüfen ───────────────────────────────────────────────
echo "[2/4] Prüfe TS-Content..."
if [ ! -f "${CONTENT_DIR}/conquer.mix" ]; then
    echo ""
    echo "FEHLER: TS-Content nicht gefunden unter:"
    echo "  ${CONTENT_DIR}/conquer.mix"
    echo ""
    echo "Einmalig installieren mit:"
    echo "  ./install-ts-content.sh"
    echo ""
    exit 1
fi
echo "      OK — conquer.mix gefunden"

# ── Schritt 3: VXL / HVA / PAL extrahieren ─────────────────────────────────────
echo "[3/4] Extrahiere ${UNIT}.vxl / ${UNIT}.hva / unittem.pal..."
TMP_DIR=$(mktemp -d)
trap "rm -rf ${TMP_DIR}" EXIT

# Die TS MIX-Dateien sind Blowfish-verschlüsselt — OpenRA's ts-Mod kann sie entschlüsseln.
# Dafür muss der Content unter dem Standard-SupportDir-Pfad liegen.
# Wir setzen einen Symlink von dort auf unser lokales Verzeichnis (einmalig).
TS_SUPPORT_DIR="${HOME}/Library/Application Support/OpenRA/Content/ts"
if [ ! -e "${TS_SUPPORT_DIR}" ]; then
    echo "      Erstelle Symlink: ${TS_SUPPORT_DIR} → ${CONTENT_DIR}"
    mkdir -p "${HOME}/Library/Application Support/OpenRA/Content"
    ln -sf "${CONTENT_DIR}" "${TS_SUPPORT_DIR}"
elif [ "$(readlink "${TS_SUPPORT_DIR}")" != "${CONTENT_DIR}" ] && [ ! -f "${TS_SUPPORT_DIR}/conquer.mix" ]; then
    echo "FEHLER: ${TS_SUPPORT_DIR} existiert bereits und enthält keinen TS-Content."
    echo "Bitte manuell prüfen."
    exit 1
fi

# ts-Mod entschlüsselt die Blowfish-geschützten MIX-Dateien automatisch.
# --extract schreibt ins aktuelle Verzeichnis → erst in TMP_DIR wechseln.
cd "${TMP_DIR}"
ENGINE_DIR="${ENGINE_DIR}" MOD_SEARCH_PATHS="${ENGINE_DIR}/mods" \
    dotnet "${ENGINE_DIR}/bin/OpenRA.Utility.dll" ts \
    --extract "${UNIT}.vxl" "${UNIT}.hva" "unittem.pal" 2>/dev/null || true
cd "${SCRIPT_DIR}"

# Dateien prüfen
MISSING=""
[ ! -f "${TMP_DIR}/${UNIT}.vxl" ] && MISSING="${MISSING} ${UNIT}.vxl"
[ ! -f "${TMP_DIR}/${UNIT}.hva" ] && MISSING="${MISSING} ${UNIT}.hva"
[ ! -f "${TMP_DIR}/unittem.pal" ] && MISSING="${MISSING} unittem.pal"

if [ -n "${MISSING}" ]; then
    echo ""
    echo "FEHLER: Diese Dateien konnten nicht extrahiert werden:${MISSING}"
    echo "Stelle sicher, dass ./install-ts-content.sh erfolgreich war."
    exit 1
fi

VXL_SIZE=$(wc -c < "${TMP_DIR}/${UNIT}.vxl")
HVA_SIZE=$(wc -c < "${TMP_DIR}/${UNIT}.hva")
echo "      ${UNIT}.vxl (${VXL_SIZE} bytes), ${UNIT}.hva (${HVA_SIZE} bytes), unittem.pal OK"

# ── Schritt 4: Rendern ─────────────────────────────────────────────────────────
echo "[4/4] Rendere ${UNIT} → ${OUT_DIR}..."
mkdir -p "${OUT_DIR}"

cd "${ENGINE_DIR}"
MOD_SEARCH_PATHS="${SCRIPT_DIR}/mods" ENGINE_DIR=".." \
    dotnet bin/OpenRA.Utility.dll cnc \
    --vxl-to-png \
    "${TMP_DIR}/${UNIT}.vxl" \
    "${TMP_DIR}/${UNIT}.hva" \
    "${TMP_DIR}/unittem.pal" \
    --facings 32 \
    --scale 12 \
    --pitch 30 \
    --yaw 225 \
    --light-yaw 240 \
    --light-pitch 50 \
    --ambient 0.6 \
    --diffuse 0.4 \
    --player-color gdi \
    --supersample 8 \
    --output-dir "${OUT_DIR}"

cd "${SCRIPT_DIR}"

echo ""
echo "=== Fertig ==="
echo "PNGs: ${OUT_DIR}/${UNIT}-0000.png .. ${OUT_DIR}/${UNIT}-0031.png"
echo ""
echo "Öffne Ergebnis..."
open "${OUT_DIR}/${UNIT}-0000.png" 2>/dev/null || echo "(open nicht verfügbar)"
