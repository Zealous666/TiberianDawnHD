# === Age of Tiberium (aotmod) - Chrome-Texte ===

button-production-types-upgrade-tooltip = Upgrades
button-production-types-navy-tooltip = Navy
button-production-types-support-tooltip = Special Units

# === NOD Airstrip Starport (Bulk-Lieferung) ===
label-deliver-in-timer = DELIVERY IN: { $time }
purchase-panel-label-delivery = DELIVERY IN:
purchase-panel-button-tooltip = Deliver all ordered units via cargo plane
button-purchase-label = Deliver

# === Superwaffen-Timer (links unten, in Spielerfarbe) ===
# Engine-Default (siehe engine/mods/ra/fluent/ra.ftl) waere "{ $player }'s { $support-power }:
# { $time }". Der Spielername entfaellt bewusst (User-Wunsch 2026-08-01) -- die Zuordnung
# passiert ueber die Textfarbe, die SupportPowerTimerWidget auf die Spielerfarbe des Besitzers
# setzt (und bei "bereit" weiss blinken laesst).
# Standard-Key (mit Spielername) -- vom Mod NICHT verwendet (ShowPlayerName: False in
# chrome/ingame.yaml), aber definiert, damit der Widget-Default nicht als fehlender Key
# gemeldet wird.
support-power-timer = { $player }'s { $support-power }: { $time }
support-power-timer-no-player = { $support-power }: { $time }
