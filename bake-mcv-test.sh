#!/bin/bash
# Bäckt den TS-MCV-Voxel als In-Game-Sprites (Zwei-Layer: RGBA-Körper + Player-Color-Overlay).
# Pipeline: EIN Render-Lauf → Body-PNGs + indizierte Overlay-Sheet →
#   Body → TGA+meta → aot-mcv-test.zip ; Overlay → aot-mcv-test-remap.png ; Installation nach bits/.
#
# Verwendung:
#   ./bake-mcv-test.sh [SCALE]
#   SCALE = Render-Scale (Pixel pro Welt-Einheit). Bestätigt: 48 (4× der Spielgröße 12).

set -e

SCALE="${1:-48}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENGINE_DIR="${SCRIPT_DIR}/engine"
BITS_DIR="${SCRIPT_DIR}/mods/cnc/bits"
TMP_DIR=$(mktemp -d)
trap "rm -rf ${TMP_DIR}" EXIT

echo "=== MCV Test-Sprite backen (scale=${SCALE}) ==="

# 1. Assets extrahieren (ts-Mod entschlüsselt Blowfish-MIX)
cd "${TMP_DIR}"
ENGINE_DIR="${ENGINE_DIR}" MOD_SEARCH_PATHS="${ENGINE_DIR}/mods" \
    dotnet "${ENGINE_DIR}/bin/OpenRA.Utility.dll" ts \
    --extract "mcv.vxl" "mcv.hva" "unittem.pal" 2>/dev/null

# 2. 32 Facings rendern
RENDER_DIR="${TMP_DIR}/render"
mkdir -p "${RENDER_DIR}"
cd "${ENGINE_DIR}"
MOD_SEARCH_PATHS="${SCRIPT_DIR}/mods" ENGINE_DIR=".." \
    dotnet bin/OpenRA.Utility.dll cnc \
    --vxl-to-png "${TMP_DIR}/mcv.vxl" "${TMP_DIR}/mcv.hva" "${TMP_DIR}/unittem.pal" \
    --facings 32 --scale "${SCALE}" --pitch 30 --yaw 225 \
    --light-yaw 240 --light-pitch 50 --ambient 0.6 --diffuse 0.4 \
    --supersample 8 --facing-offset 45 --facing-flip \
    --remap-sheet "${TMP_DIR}/aot-mcv-test-remap.png" \
    --output-dir "${RENDER_DIR}" 2>&1 | grep -E "Voxels|Saved|overlay"

# 3. PNG → unkomprimiertes 32-bit TGA + meta
STAGE="${TMP_DIR}/stage"
mkdir -p "${STAGE}"
python3 - "${RENDER_DIR}" "${STAGE}" <<'PYEOF'
import sys, os, struct
from PIL import Image
render_dir, stage = sys.argv[1], sys.argv[2]

def write_tga(path, img):
    w, h = img.size
    px = img.load()
    hdr = bytes([0,0,2, 0,0,0,0,0, 0,0, 0,0]) + struct.pack('<HH', w, h) + bytes([32, 0x28])
    out = bytearray(hdr)
    for y in range(h):
        for x in range(w):
            r,g,b,a = px[x,y]
            out += bytes([b,g,r,a])
    with open(path, 'wb') as f:
        f.write(out)

for i in range(32):
    img = Image.open(os.path.join(render_dir, f"mcv-{i:04d}.png")).convert("RGBA")
    w, h = img.size
    write_tga(os.path.join(stage, f"mcv-{i:04d}.tga"), img)
    with open(os.path.join(stage, f"mcv-{i:04d}.meta"), 'w') as f:
        f.write(f'{{"size":[{w},{h}],"crop":[0,0,{w},{h}]}}')
print(f"TGA+meta: 32× {w}x{h}")
PYEOF

# 4. Body-ZIP packen + Overlay-Sheet installieren
cd "${STAGE}"
zip -q -X "${TMP_DIR}/aot-mcv-test.zip" mcv-*.tga mcv-*.meta
cp "${TMP_DIR}/aot-mcv-test.zip" "${BITS_DIR}/aot-mcv-test.zip"
cp "${TMP_DIR}/aot-mcv-test-remap.png" "${BITS_DIR}/aot-mcv-test-remap.png"

echo "=== Fertig ==="
echo "  Body:    ${BITS_DIR}/aot-mcv-test.zip"
echo "  Overlay: ${BITS_DIR}/aot-mcv-test-remap.png"
