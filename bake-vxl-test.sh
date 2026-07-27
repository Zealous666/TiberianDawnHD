#!/bin/bash
# Bäckt ein beliebiges TS-Voxel als In-Game-Sprites (Zwei-Layer: RGBA-Körper + Player-Color-Overlay).
# Pipeline: EIN Render-Lauf → Body-PNGs + indizierte Overlay-Sheet →
#   Body → TGA+meta → aot-<OUT>-test.zip ; Overlay → aot-<OUT>-test-remap.png ; Installation nach bits/.
#
# Verwendung:
#   ./bake-vxl-test.sh <VXL> [SCALE] [OUT] [ARCHIVE_DIR] [COLOR]
#   VXL         = Voxel-Basisname im TS-Content (z.B. mcv, apache)
#   SCALE       = Render-Scale (Pixel/Welt-Einheit). Default 48 (4× der Spielgröße 12).
#   OUT         = Asset-Basisname (Default = VXL). Z.B. apache → harpy.
#   ARCHIVE_DIR = optional: Verzeichnis für komplette Einzel-PNGs (Body+Player-Color zusammengefügt)
#                 zum Archivieren. KONVENTION: mods/management/asset wip/<unit>/ts
#   COLOR       = Fraktionsfarbe fürs Archiv-Composite (gdi|nod|#RRGGBB). Default gdi.
#
# Beispiele:
#   ./bake-vxl-test.sh mcv 48 mcv "mods/management/asset wip/mcv/ts" gdi
#   ./bake-vxl-test.sh apache 48 harpy "mods/management/asset wip/hind/ts" nod

set -e

VXL="${1:?VXL-Name fehlt (z.B. apache)}"
SCALE="${2:-48}"
OUT="${3:-$VXL}"
ARCHIVE_DIR="${4:-}"
COLOR="${5:-gdi}"
# Body-Helligkeit (nur Nicht-Player-Color). 1.0 = unverändert, 0.9 = 10% dunkler. Via Env:
#   BRIGHTNESS=0.9 ./bake-vxl-test.sh ...
BRIGHTNESS="${BRIGHTNESS:-1.0}"
# Body-Sättigung (nur Nicht-Player-Color). 1.0 = volle Farbe, 0.1 = 90% desaturiert (fast silbergrau). Via Env:
#   SATURATION=0.1 ./bake-vxl-test.sh ...
SATURATION="${SATURATION:-1.0}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENGINE_DIR="${SCRIPT_DIR}/engine"
BITS_DIR="${SCRIPT_DIR}/mods/cnc/bits"
TMP_DIR=$(mktemp -d)
trap "rm -rf ${TMP_DIR}" EXIT

echo "=== Voxel-Test backen: ${VXL}.vxl → aot-${OUT}-test (scale=${SCALE}) ==="

# 1. Assets extrahieren (ts-Mod entschlüsselt Blowfish-MIX)
cd "${TMP_DIR}"
ENGINE_DIR="${ENGINE_DIR}" MOD_SEARCH_PATHS="${ENGINE_DIR}/mods" \
    dotnet "${ENGINE_DIR}/bin/OpenRA.Utility.dll" ts \
    --extract "${VXL}.vxl" "${VXL}.hva" "unittem.pal" 2>/dev/null
# Remap floor: maps [floor, 1.0] brightness → full ramp. Formula: ambient - 0.05.
# Default ambient=0.6 → floor=0.55. Override via env: REMAP_FLOOR=0.45 ./bake-vxl-test.sh ...
REMAP_FLOOR="${REMAP_FLOOR:-0.55}"

# 2. 32 Facings rendern (Body + Player-Color-Overlay-Sheet)
RENDER_DIR="${TMP_DIR}/render"
mkdir -p "${RENDER_DIR}"
cd "${ENGINE_DIR}"
MOD_SEARCH_PATHS="${SCRIPT_DIR}/mods" ENGINE_DIR=".." \
    dotnet bin/OpenRA.Utility.dll cnc \
    --vxl-to-png "${TMP_DIR}/${VXL}.vxl" "${TMP_DIR}/${VXL}.hva" "${TMP_DIR}/unittem.pal" \
    --facings 32 --scale "${SCALE}" --pitch 30 --yaw 225 \
    --light-yaw 240 --light-pitch 50 --ambient 0.6 --diffuse 0.4 \
    --supersample 8 --facing-offset 45 --facing-flip --brightness "${BRIGHTNESS}" --saturation "${SATURATION}" \
    --remap-sheet "${TMP_DIR}/aot-${OUT}-test-remap.png" --remap-floor "${REMAP_FLOOR}" \
    --output-dir "${RENDER_DIR}" 2>&1 | grep -E "Voxels|Saved|overlay"

# 3. PNG → unkomprimiertes 32-bit TGA + meta
STAGE="${TMP_DIR}/stage"
mkdir -p "${STAGE}"
python3 - "${RENDER_DIR}" "${STAGE}" "${VXL}" "${OUT}" <<'PYEOF'
import sys, os, struct
from PIL import Image
render_dir, stage, vxl, out = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

def write_tga(path, img):
    w, h = img.size
    px = img.load()
    hdr = bytes([0,0,2, 0,0,0,0,0, 0,0, 0,0]) + struct.pack('<HH', w, h) + bytes([32, 0x28])
    o = bytearray(hdr)
    for y in range(h):
        for x in range(w):
            r,g,b,a = px[x,y]
            o += bytes([b,g,r,a])
    with open(path, 'wb') as f:
        f.write(o)

for i in range(32):
    img = Image.open(os.path.join(render_dir, f"{vxl}-{i:04d}.png")).convert("RGBA")
    w, h = img.size
    write_tga(os.path.join(stage, f"{out}-{i:04d}.tga"), img)
    with open(os.path.join(stage, f"{out}-{i:04d}.meta"), 'w') as f:
        f.write(f'{{"size":[{w},{h}],"crop":[0,0,{w},{h}]}}')
print(f"TGA+meta: 32× {w}x{h}")
PYEOF

# 4. Body-ZIP packen + Overlay-Sheet installieren
cd "${STAGE}"
zip -q -X "${TMP_DIR}/aot-${OUT}-test.zip" ${OUT}-*.tga ${OUT}-*.meta
cp "${TMP_DIR}/aot-${OUT}-test.zip" "${BITS_DIR}/aot-${OUT}-test.zip"
cp "${TMP_DIR}/aot-${OUT}-test-remap.png" "${BITS_DIR}/aot-${OUT}-test-remap.png"

# 5. Archiv: komplette Einzel-PNGs (Body + Player-Color zusammengefügt) — KONVENTION für alle TS-Exports
if [ -n "${ARCHIVE_DIR}" ]; then
    ABS_ARCHIVE="${ARCHIVE_DIR}"
    case "${ABS_ARCHIVE}" in /*) ;; *) ABS_ARCHIVE="${SCRIPT_DIR}/${ARCHIVE_DIR}" ;; esac
    mkdir -p "${ABS_ARCHIVE}"
    cd "${ENGINE_DIR}"
    MOD_SEARCH_PATHS="${SCRIPT_DIR}/mods" ENGINE_DIR=".." \
        dotnet bin/OpenRA.Utility.dll cnc \
        --vxl-to-png "${TMP_DIR}/${VXL}.vxl" "${TMP_DIR}/${VXL}.hva" "${TMP_DIR}/unittem.pal" \
        --facings 32 --scale "${SCALE}" --pitch 30 --yaw 225 \
        --light-yaw 240 --light-pitch 50 --ambient 0.6 --diffuse 0.4 \
        --supersample 8 --facing-offset 45 --facing-flip --brightness "${BRIGHTNESS}" --saturation "${SATURATION}" \
        --player-color "${COLOR}" \
        --output-dir "${ABS_ARCHIVE}" 2>&1 | grep -E "Saved"
    # Frames auf OUT-Namen umbenennen (Renderer nutzt VXL-Basisnamen)
    if [ "${OUT}" != "${VXL}" ]; then
        for f in "${ABS_ARCHIVE}/${VXL}-"*.png; do
            [ -e "$f" ] || continue
            mv "$f" "${ABS_ARCHIVE}/${OUT}-$(basename "$f" | sed "s/^${VXL}-//")"
        done
    fi
fi

echo "=== Fertig ==="
echo "  Body:    ${BITS_DIR}/aot-${OUT}-test.zip"
echo "  Overlay: ${BITS_DIR}/aot-${OUT}-test-remap.png"
[ -n "${ARCHIVE_DIR}" ] && echo "  Archiv:  ${ABS_ARCHIVE}/ (32 PNGs, ${COLOR})"
