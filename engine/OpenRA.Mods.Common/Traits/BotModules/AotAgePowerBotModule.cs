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

		[Desc("Credits the Age fund never touches, so the base can keep building while it saves.",
			"Under the research model the upgrade is buyable the moment its prerequisites are met,",
			"so without a floor the bot would hoard every credit from that point on and stall its",
			"own production for the whole saving period. The user's spec is that it saves MOST of",
			"its income for the age, not literally all of it.")]
		public readonly int SavingReserve = 1000;

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

			foreach (var key in Info.PowerOrderNames)
			{
				if (emergency)
				{
					savings.TryGetValue(key, out var held);
					Refund(key, held);
					continue;
				}

				savings.TryGetValue(key, out var saved);

				if (!supportPowerManager.Powers.TryGetValue(key, out var power) || power.Disabled)
				{
					// Power weg (noch nicht freigeschaltet, schon gekauft, Spieler verloren):
					// Zurueckgelegtes sofort freigeben, sonst waere es fuer immer verloren.
					Refund(key, saved);
					continue;
				}

				if (power.Info is not AutoActivateSpawnActorPowerInfo info || info.Cost <= 0)
					continue;

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
					// Kauf-Modus mit HOECHSTER Prioritaet (User-Wunsch 2026-08-04): solange der
					// volle Betrag nicht zusammen ist, greift das Modul in JEDEM Bot-Tick alles ab,
					// was da ist -- vor allen anderen Modulen. Normalerweise liegt das Geld nach
					// der Ladezeit ohnehin komplett hier; der Fall greift, wenn zwischen Rueckgabe
					// und Wirksamwerden der Order ein anderes Modul zugeschlagen hat.
					if (saved < info.Cost)
					{
						saved = Save(key, saved, info.Cost - saved);
						if (saved < info.Cost)
							continue;
					}

					// Erst zurueckgeben, dann ausloesen: die Power bucht beim Aktivieren selbst ab
					// (AutoActivateSpawnActorPower.Activate). Schlaegt die Order fehl, faellt der
					// Betrag als loses Geld an und wird im naechsten Tick oben wieder eingesammelt
					// -- die Schleife laeuft also von selbst weiter, bis der Kauf sitzt.
					Refund(key, saved);
					world.IssueOrder(new Order(key, supportPowerManager.Self, false));
					continue;
				}

				if (!power.Active || power.TotalTicks <= 0)
					continue;

				// Gleichmaessig ansparen: Kosten geteilt durch Gesamtdauer, also bei halb geladener
				// Power die Haelfte der Kosten. Ueber den Ziel-Ist-Vergleich statt eines festen
				// Betrags pro Tick, damit sich verpasste Raten (kein Geld da) spaeter von selbst
				// nachholen. Bewusst OHNE Deckel auf einen Anteil des Vermoegens: dem Cashflow soll
				// nur die laufende Rate entzogen werden, so als wuerde die KI etwas bauen.
				var elapsed = power.TotalTicks - power.RemainingTicks;
				var target = info.Cost * elapsed / power.TotalTicks;
				if (target > saved)
					Save(key, saved, target - saved);
			}
		}

		// Legt bis zu "wanted" zur Seite und gibt den neuen Kontostand zurueck. Ist weniger da,
		// wird genommen was da ist -- die Rate wird dann beim naechsten Mal nachgeholt, weil sich
		// das Sparziel aus dem Ladefortschritt ergibt und nicht aus einer Summe von Einzelraten.
		int Save(string key, int saved, int wanted)
		{
			// Leave the reserve liquid -- see SavingReserve.
			var available = playerResources.GetCashAndResources() - Info.SavingReserve;
			if (available <= 0)
				return saved;

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
