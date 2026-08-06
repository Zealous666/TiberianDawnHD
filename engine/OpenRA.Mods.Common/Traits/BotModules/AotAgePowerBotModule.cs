// === Age of Tiberium (aotmod) ===
// Bot-Verhalten fuer die Age-Aufstiegs-Powers (AotAge1/2/3Power auf dem Spieler-Aktor).
//
// Ausgangslage: der Aufstieg ist keine Produktionsqueue mehr, sondern eine ganz normale Super
// Power -- sie laedt von selbst, sobald die Voraussetzungen stehen, und muss dann per Klick
// ausgeloest werden, wobei die Kosten EINMALIG abgebucht werden. Die KI klickt nicht, und die
// vorhandenen Bot-Module kaufen nur aus Produktionsqueues -- ohne dieses Modul steigt sie nie auf.
//
// Zwei Aufgaben:
//   1. Ausloesen, sobald die Power fertig geladen ist.
//   2. Ansparen. Das ist der eigentliche Grund fuer das Modul: eine fertig geladene Power nuetzt
//      nichts, wenn die KI das Geld gerade in Panzer gesteckt hat. Waehrend der Ladezeit wandert
//      deshalb laufend ein Anteil der Kosten auf ein modul-eigenes Konto -- linear ueber genau
//      denselben Zeitraum, den die Power zum Laden braucht. Bei 100% Ladung liegt der volle
//      Betrag bereit.
//
// Warum das Geld WIRKLICH abgebucht wird (TakeCash) statt nur virtuell reserviert: alle anderen
// Bot-Module (BaseBuilder, UnitBuilder, ...) fragen PlayerResources direkt und kennen kein
// Reservierungskonzept. Nur echtes Abbuchen haelt sie zuverlaessig davon ab, das Geld
// auszugeben. Beim Ausloesen wird der Betrag zurueckgegeben und von der Power regulaer bezahlt.
//
// Silo-Obergrenzen muessen dafuer NICHT angefasst werden: ResourceCapacity deckelt nur
// PlayerResources.Resources (das ungemuenzte Erz). Was hier auf dem Konto liegt, ist aus
// PlayerResources heraus und damit unbegrenzt. Nebeneffekt: das Ansparen macht Silo-Platz frei
// und verringert die Verschwendung bei vollen Silos eher, als dass es sie erhoeht.
// === Ende aotmod ===

using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Frozen;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Lets the bot save up for and trigger the Age of Tiberium age-advance support powers.")]
	public class AotAgePowerBotModuleInfo : ConditionalTraitInfo, Requires<SupportPowerManagerInfo>
	{
		[FieldLoader.Require]
		[Desc("Support power order names to handle, e.g. AotAge1PowerInfoOrder.",
			"SupportPowerInfo builds this as <InfoTypeName>Order.")]
		public readonly FrozenSet<string> PowerOrderNames = FrozenSet<string>.Empty;

		[Desc("Percentage of INCOME the Age fund skims while it saves. This is deliberately a share",
			"of what comes in, not a grab from the balance: the module used to take everything on the",
			"account the moment the prerequisites were met, which brought the bot's whole operation to",
			"a halt for as long as it took to reach the price (User 2026-08-05: \"damit wuerde sie de",
			"facto fuer lange zeit ihren betrieb einstellen\"). Skimming income keeps production",
			"running throughout and makes the saving time predictable: at 55% and 1500 credits a",
			"minute, 5000 takes about six minutes.")]
		public readonly int IncomeShare = 55;

		[Desc("Cash at which the fund switches from skimming income to saving in earnest, provided",
			"the upgrade is already buyable (its building stands). This is how a human plays it:",
			"put up the structures, build a wave's worth of units, and once roughly this much is",
			"left, stop spending and take the rest straight to the Age price (User 2026-08-05).",
			"Once tripped it latches until the upgrade is bought.")]
		public readonly int HardSavingTrigger = 3000;

		public override object Create(ActorInitializer init) { return new AotAgePowerBotModule(init.Self, this); }
	}

	public class AotAgePowerBotModule : ConditionalTrait<AotAgePowerBotModuleInfo>, IBotTick
	{
		readonly World world;

		// Pro Power zurueckgelegter Betrag. Liegt bewusst NICHT in PlayerResources.
		readonly Dictionary<string, int> savings = [];

		SupportPowerManager supportPowerManager;
		PlayerResources playerResources;
		AotOperationsBotModule[] ops = [];

		public AotAgePowerBotModule(Actor self, AotAgePowerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
		}

		protected override void Created(Actor self)
		{
			supportPowerManager = self.Owner.PlayerActor.Trait<SupportPowerManager>();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();

			// TraitsImplementing, not TraitOrDefault: the player actor carries one operations module
			// per faction and TraitOrDefault throws on the second one.
			ops = self.Owner.PlayerActor.TraitsImplementing<AotOperationsBotModule>().ToArray();
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Saving for the next Age is the single highest-priority spend -- EXCEPT while the economy
			// itself is gone (User-Fund 2026-08-04). A bot that has lost its last ore transporter, with
			// no refinery and no harvester, has no income at all; hoarding 5000 credits for an Age
			// upgrade while the replacement transporter cannot be paid for is exactly backwards. Save()
			// takes the money off the account via TakeCash, so this module really does starve the
			// replacement. Anything already put aside is released so it can be spent on the transporter.
			//
			// A base expansion that holds priority is the second exception (User-Fund 2026-08-04). It
			// saves for its MCV and escort out of the same account, so an Age fund quietly draining the
			// balance meant the expansion could never reach its own threshold -- it just planned, waited
			// and expired, over and over. Priority is bounded by ExpansionPriorityTimeout, so this
			// stand-down cannot last forever even if the expansion never gets off the ground.
			var emergency = ops.Any(o => o.EconomyEmergency() || o.ExpansionHoldsPriority());

			// One research at a time; while it runs, the fund works ahead on the next tier.
			AotAgeResearchInstance running = null;
			foreach (var k in Info.PowerOrderNames)
				if (supportPowerManager.Powers.TryGetValue(k, out var p)
					&& p is AotAgeResearchInstance r && r.Researching)
					running = r;

			var anyResearching = running != null;

			// ONLY THE NEXT TIER IS FUNDED. All three Ages sit in PowerOrderNames, and while Age 1 is
			// being researched both Age 2 AND Age 3 are "unpurchased and still locked" -- so without
			// this the fund would pace towards 7500 and 10000 at the same time and starve the base for
			// a tier two steps away. The next tier is simply the first one not yet bought.
			string nextKey = null;
			foreach (var k in Info.PowerOrderNames)
			{
				if (supportPowerManager.Powers.TryGetValue(k, out var p2)
					&& p2 is AotAgeResearchInstance r2 && r2.Purchased)
					continue;

				nextKey = k;
				break;
			}

			// Income since the last bot tick. Earned only ever grows, so the delta is what actually
			// came in -- spending does not distort it the way a balance comparison would.
			var income = Math.Max(0, playerResources.Earned - lastEarned);
			lastEarned = playerResources.Earned;
			skim = income * Info.IncomeShare / 100;

			foreach (var key in Info.PowerOrderNames)
			{
				if (emergency)
				{
					hardSaving.Remove(key);
					savings.TryGetValue(key, out var held);
					Refund(key, held);
					continue;
				}

				savings.TryGetValue(key, out var saved);

				if (!supportPowerManager.Powers.TryGetValue(key, out var power))
				{
					Refund(key, saved);
					continue;
				}

				// Anything beyond the next tier releases whatever it holds -- see nextKey above.
				if (key != nextKey)
				{
					hardSaving.Remove(key);
					Refund(key, saved);
					continue;
				}

				if (power.Info is not AutoActivateSpawnActorPowerInfo info || info.Cost <= 0)
					continue;

				// Disabled covers three very different things, and they must not share a branch:
				// already bought, player lost, or simply not unlocked yet. Only the last one is worth
				// keeping money for -- and only while an earlier age is researching, which is exactly
				// the pre-funding window. Anything else releases the savings, or they would sit there
				// for the rest of the match.
				if (power.Disabled)
				{
					var bought = power is AotAgeResearchInstance done && done.Purchased;
					if (!bought && anyResearching)
					{
						// PACED TO THE RESEARCH WINDOW (User 2026-08-05: "genau in den 15 minuten soll
						// er doch schon den GESAMTEN betrag fuer age-2 zuruecklegen"). The target is
						// tied to how far the running research has got, so the next tier's full price
						// is banked exactly as this one completes -- and the rest of the income stays
						// free for the buildings that were held back during the sprint.
						//
						// An absolute target rather than a rate per tick: a moment with no money to
						// spare is made up automatically later instead of being lost for good.
						if (running.TotalTicks > 0)
						{
							var elapsed = running.TotalTicks - running.RemainingTicks;
							var target = info.Cost * elapsed / running.TotalTicks;
							if (target > saved)
								Save(key, saved, target - saved);
						}
					}

					// NOT refunded when no research is running: the whole point of banking it during
					// the window is that it is ready the moment the tier unlocks (User 2026-08-05:
					// "wenn sie ein neues age erreicht hat, hat sie das geld schon beiseite gelegt und
					// kann zu dem zeitpunkt abgerufen werden"). Releasing it the instant the research
					// ended -- which is exactly when the Science Lab or Temple is still going up --
					// would throw away everything the pacing just achieved. It simply stops GROWING
					// while the tier is locked and no window is running; an economy emergency still
					// releases it.

					continue;
				}

				// RESEARCH MODEL: once the upgrade is bought and the research is running, it is paid
				// for -- there is nothing left to save towards. Without this the module would fall
				// through to the pro-rata branch below, whose target grows with elapsed charge time,
				// and would quietly hoard the full price a SECOND time for an upgrade already owned.
				if (power is AotAgeResearchInstance research && research.Researching)
				{
					Refund(key, saved);
					continue;
				}

				if (power.Ready)
				{
					// Enough put by -- hand it back and buy. Refund first, because the power bills the
					// account itself when the order resolves. If the order fails the money is simply
					// loose again and gets skimmed back next tick, so the loop settles by itself.
					if (saved >= info.Cost)
					{
						hardSaving.Remove(key);
						Refund(key, saved);
						world.IssueOrder(new Order(key, supportPowerManager.Self, false));
						continue;
					}

					// While a research is running, PACE instead of sprinting -- even if this tier is
					// already unlocked. The sprint exists to close the last gap before a purchase; if
					// it engaged during the research window it would freeze the base all over again,
					// in exactly the window the buildings held back by the previous sprint are meant
					// to go up. Pacing banks the full price by the time the window ends anyway.
					if (anyResearching && running.TotalTicks > 0)
					{
						var elapsed = running.TotalTicks - running.RemainingTicks;
						var target = info.Cost * elapsed / running.TotalTicks;
						if (target > saved)
							Save(key, saved, target - saved);

						continue;
					}

					// The sprint starts once there is a real pile to build on -- everything already put
					// by, plus what is on the account. Below that the bot keeps operating normally on
					// the income share; above it, every credit goes to the Age.
					if (saved + playerResources.GetCashAndResources() >= Info.HardSavingTrigger)
						hardSaving.Add(key);

					if (hardSaving.Contains(key))
						Save(key, saved, info.Cost - saved);
					else
						SaveFromIncome(key, saved, info.Cost);

					continue;
				}

				if (!power.Active || power.TotalTicks <= 0)
				{
					// Prerequisites not up yet. Pre-funding the NEXT age is worth doing only while an
					// earlier one is actually being researched: that window is dead time for this fund
					// anyway, so the money is already waiting when the next tier unlocks (the user's
					// second idea, kept as the complement to the income share). Outside that window the
					// savings are released -- money sitting idle for an age whose Temple or Shrine is
					// not even planned yet is just another way of standing still.
					if (anyResearching)
						SaveFromIncome(key, saved, info.Cost);
					else
						Refund(key, saved);

					continue;
				}

				// Active but not Ready means the research is running (handled above) -- nothing to do.
			}
		}

		// Legt bis zu "wanted" zur Seite und gibt den neuen Kontostand zurueck. Ist weniger da,
		// wird genommen was da ist -- die Rate wird dann beim naechsten Mal nachgeholt, weil sich
		// das Sparziel aus dem Ladefortschritt ergibt und nicht aus einer Summe von Einzelraten.
		int lastEarned;
		int skim;

		// Latched per power: once the bot commits to the sprint it does not drift back out of it
		// because a unit finished and the balance dipped under the trigger again.
		readonly HashSet<string> hardSaving = [];

		// Read by AotOperationsBotModule: attack waves stand down while this is on, exactly as a human
		// stops producing to reach the next Age. Base defence is deliberately NOT affected -- being
		// overrun while saving would be the one way this backfires.
		public bool HardSaving => hardSaving.Count > 0;

		// Has this bot committed to its first Age yet? Read by AotOperationsBotModule to hold the very
		// first attack wave until then.
		public bool AnyAgeStarted =>
			Info.PowerOrderNames.Any(k => supportPowerManager.Powers.TryGetValue(k, out var p)
				&& p is AotAgeResearchInstance r && r.Purchased);

		// Skims this tick's share of income towards `cost`, never touching what is already banked for
		// other purposes.
		void SaveFromIncome(string key, int saved, int cost)
		{
			var wanted = Math.Min(skim, cost - saved);
			if (wanted > 0)
				Save(key, saved, wanted);
		}

		int Save(string key, int saved, int wanted)
		{
			// Never more than is actually on the account -- the share is computed by the caller from
			// income, this is only the hard limit.
			var available = playerResources.GetCashAndResources();
			var take = wanted < available ? wanted : available;
			if (take <= 0 || !playerResources.TakeCash(take))
				return saved;

			savings[key] = saved + take;
			return saved + take;
		}

		void Refund(string key, int saved)
		{
			if (saved <= 0)
				return;

			playerResources.GiveCash(saved);
			savings[key] = 0;
		}

		protected override void TraitDisabled(Actor self)
		{
			foreach (var key in Info.PowerOrderNames)
				if (savings.TryGetValue(key, out var saved))
					Refund(key, saved);
		}
	}
}
