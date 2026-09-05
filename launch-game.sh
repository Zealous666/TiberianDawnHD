#!/bin/sh

set -o errexit || exit $?

require_variables() {
	missing=""
	for i in "$@"; do
		eval check="\$$i"
		[ -z "${check}" ] && missing="${missing}   ${i}\n"
	done
	if [ ! -z "${missing}" ]; then
		echo "Required mod.config variables are missing:\n${missing}Repair your mod.config (or user.config) and try again."
		exit 1
	fi
}

TEMPLATE_LAUNCHER=$(python3 -c "import os; print(os.path.realpath('$0'))")
TEMPLATE_ROOT=$(dirname "${TEMPLATE_LAUNCHER}")
MOD_SEARCH_PATHS="${TEMPLATE_ROOT}/mods"

# shellcheck source=mod.config
. "${TEMPLATE_ROOT}/mod.config"

if [ -f "${TEMPLATE_ROOT}/user.config" ]; then
	# shellcheck source=user.config
	. "${TEMPLATE_ROOT}/user.config"
fi

require_variables "MOD_ID" "ENGINE_VERSION" "ENGINE_DIRECTORY"

cd "${TEMPLATE_ROOT}"
if [ ! -f "${ENGINE_DIRECTORY}/bin/OpenRA.dll" ] || [ "$(cat "${ENGINE_DIRECTORY}/VERSION")" != "${ENGINE_VERSION}" ]; then
	echo "Required engine files not found."
	echo "Run \`make\` in the mod directory to fetch and build the required files, then try again.";
	exit 1
fi

cd "${ENGINE_DIRECTORY}"
# Exit-Code auffangen statt das Skript hier beenden zu lassen: `set -o errexit` steht oben, ein
# Absturz oder ein Beenden per Fenster-X wuerde das Archivieren unten sonst ueberspringen -- also
# genau in den Faellen, in denen das Log am interessantesten ist.
GAME_EXIT=0
# Zeitmarke VOR dem Start: unten wird nur kopiert, was seither geschrieben wurde. Ohne das
# landen die ueber Monate angesammelten exception-*.log jedes Mal mit im Archiv (gemessen:
# 119 Dateien / 300 MB pro Lauf).
RUN_STAMP=$(mktemp)
dotnet bin/OpenRA.dll Game.Mod="${MOD_ID}" Engine.EngineDir=".." Engine.LaunchPath="${TEMPLATE_LAUNCHER}" Engine.ModSearchPaths="${MOD_SEARCH_PATHS}" "$@" || GAME_EXIT=$?

# === Log-Archiv bei Spielende (User-Wunsch 2026-08-24) ===
# OpenRA legt debug.log bei JEDEM Start NEU an (File.CreateText in Log.AddChannel) -- der
# vorherige Lauf ist damit unwiederbringlich weg. Zwei Laeufe liessen sich deshalb nie
# vergleichen: eine KI-Diagnose ("war es vorher besser?") war genau daran nicht zu beantworten.
# Kopiert wird NACH dem Spiel, damit die Dateien vollstaendig und geschlossen sind.
LOG_DIR=""
for candidate in \
	"${HOME}/Library/Application Support/OpenRA/Logs" \
	"${HOME}/.config/openra/Logs" \
	"${HOME}/.openra/Logs"; do
	if [ -d "${candidate}" ]; then
		LOG_DIR="${candidate}"
		break
	fi
done

if [ -n "${LOG_DIR}" ]; then
	ARCHIVE_ROOT="${TEMPLATE_ROOT}/logs-archive"
	ARCHIVE="${ARCHIVE_ROOT}/$(date +%Y%m%d-%H%M%S)"
	mkdir -p "${ARCHIVE}"
	# Nur Dateien, die WAEHREND dieses Laufs geschrieben wurden (siehe RUN_STAMP oben).
	# || true: ein Lauf ohne jede *.log-Datei ist kein Grund, mit Fehler abzubrechen.
	find "${LOG_DIR}" -maxdepth 1 -type f -name '*.log' -newer "${RUN_STAMP}" \
		-exec cp {} "${ARCHIVE}/" \; 2>/dev/null || true
	rm -f "${RUN_STAMP}"
	if [ -n "$(ls -A "${ARCHIVE}" 2>/dev/null)" ]; then
		echo "Logs archiviert: ${ARCHIVE}"
	else
		rmdir "${ARCHIVE}" 2>/dev/null || true
	fi

	# Nur die letzten 30 Laeufe behalten -- ein Log-Satz ist mehrere MB gross.
	ls -1d "${ARCHIVE_ROOT}"/*/ 2>/dev/null | sort -r | tail -n +31 | while read -r old; do
		rm -rf "${old}"
	done
fi

exit ${GAME_EXIT}
