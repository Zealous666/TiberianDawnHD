// === Age of Tiberium (aotmod) ===
// Schreibt in festen Abstaenden den Finanzstand JEDES Spielers ins debug.log (User-Wunsch
// 2026-08-04). Zweck ist Balance-Beobachtung waehrend eines laufenden Tests: "ab spaeterem Age 1
// ist zu viel Cash im Umlauf" laesst sich nur beurteilen, wenn man den Verlauf sieht -- die
// Ingame-Anzeige zeigt nur den eigenen Kontostand und nur den Moment.
//
// Bewusst eine Zeile pro Spieler und Intervall, mit festem Praefix [AotCash], damit sich der
// Verlauf spaeter mit einem simplen grep herausziehen laesst.
// === Ende aotmod ===

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Periodically logs the player's cash, resources and totals to debug.log for balance analysis.")]
	public class AotCashLogInfo : TraitInfo, Requires<PlayerResourcesInfo>
	{
		[Desc("Ticks between log lines. 250 = every 10 seconds at the default timestep.")]
		public readonly int Interval = 250;

		[Desc("Also log players that are not participating (spectators, map-owned neutrals).")]
		public readonly bool IncludeNonCombatants = false;

		[Desc("Length of the window the income average is taken over, in ticks. 1500 = one minute.",
			"Deliberately much longer than Interval: harvester deliveries arrive in lumps, so a",
			"rate extrapolated from a single interval swings wildly and says nothing.")]
		public readonly int IncomeWindow = 1500;

		public override object Create(ActorInitializer init) { return new AotCashLog(init.Self, this); }
	}

	public class AotCashLog : ITick
	{
		readonly AotCashLogInfo info;
		readonly Player player;
		readonly PlayerResources resources;
		int ticks;

		// Ringpuffer ueber IncomeWindow: der interessante Wert beim Balancing ist nicht das
		// Gesamtvermoegen, sondern wie schnell es nachwaechst -- und zwar geglaettet.
		readonly Queue<(int Tick, int Earned)> history = [];

		// Farbe des Spielers, damit sich die Log-Zeilen den Parteien im Spiel zuordnen lassen
		// (User 2026-08-04: die Bot-Namen sind im Log alle identisch). Der NAME ist nur die
		// naechstgelegene aus der 8-Farben-Palette des Mods -- die Lobby laesst einen freien
		// HSL-Regler zu, ein Treffer ist also nicht garantiert. Deshalb steht der exakte
		// Hex-Wert immer mit dabei.
		string colorLabel;

		public AotCashLog(Actor self, AotCashLogInfo info)
		{
			this.info = info;
			player = self.Owner;
			resources = self.Trait<PlayerResources>();
		}

		string ColorLabel()
		{
			if (colorLabel != null)
				return colorLabel;

			var c = player.Color;
			var best = "?";
			var bestDist = int.MaxValue;
			foreach (var (_, label, candidate) in Render.RenderSpritesInfo.AotEditorColors)
			{
				var dr = c.R - candidate.R;
				var dg = c.G - candidate.G;
				var db = c.B - candidate.B;
				var dist = (dr * dr) + (dg * dg) + (db * db);
				if (dist < bestDist)
				{
					bestDist = dist;
					best = label;
				}
			}

			return colorLabel = $"{best}/{c.R:X2}{c.G:X2}{c.B:X2}";
		}

		void ITick.Tick(Actor self)
		{
			if (--ticks > 0)
				return;

			ticks = info.Interval;

			if (!info.IncludeNonCombatants && player.NonCombatant)
				return;

			var now = self.World.WorldTick;
			history.Enqueue((now, resources.Earned));
			while (history.Count > 1 && now - history.Peek().Tick > info.IncomeWindow)
				history.Dequeue();

			var oldest = history.Peek();
			var spanTicks = now - oldest.Tick;
			var ticksPerMinute = 60000 / self.World.Timestep;
			var perMinute = spanTicks > 0 ? (resources.Earned - oldest.Earned) * ticksPerMinute / spanTicks : 0;

			var elapsed = WidgetUtilsTime(now, self.World.Timestep);

			Log.Write("debug",
				$"[AotCash] {elapsed} {player.InternalName} '{player.PlayerName}' " +
				$"[{ColorLabel()}] ({player.Faction.InternalName}{(player.IsBot ? ", bot" : "")}) " +
				$"cash={resources.Cash} ore={resources.Resources}/{resources.ResourceCapacity} " +
				$"total={resources.GetCashAndResources()} earned={resources.Earned} spent={resources.Spent} " +
				$"income/min={perMinute}");
		}

		// Spielzeit als mm:ss -- WidgetUtils liegt in OpenRA.Mods.Common.Widgets und soll hier
		// nicht mit hereingezogen werden, die Rechnung ist trivial genug.
		static string WidgetUtilsTime(int worldTick, int timestep)
		{
			var seconds = worldTick * timestep / 1000;
			return $"{seconds / 60:D2}:{seconds % 60:D2}";
		}
	}
}
