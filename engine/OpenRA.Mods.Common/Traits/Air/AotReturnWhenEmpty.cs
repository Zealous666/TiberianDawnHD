#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * When all ammo pools are depleted mid-attack, the aircraft saves the attack target,
 * returns to a rearm pad, and resumes the attack after rearming.
 * If no attack was in progress when ammo ran out, it hovers in place.
 */
#endregion

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("When ammo is depleted during an attack, returns to rearm pad and resumes the attack afterwards. Hovers in place when idle.")]
	public class AotReturnWhenEmptyInfo : TraitInfo, Requires<AircraftInfo>, Requires<RearmableInfo>
	{
		public override object Create(ActorInitializer init) { return new AotReturnWhenEmpty(init, this); }
	}

	public class AotReturnWhenEmpty : INotifyCreated, ITick, INotifyDockClient, INotifyAttack, IResolveOrder
	{
		readonly AotReturnWhenEmptyInfo info;
		AmmoPool[] ammoPools;
		Armament[] armaments;
		bool wasEmpty;
		bool hasResumeTarget;
		Target resumeTarget;
		Target lastAttackTarget;

		// aotmod: Position, an der der Heli beim Leerschuss stand -> nach dem Rearm fliegt er DORTHIN
		// zurueck (nicht ans Pad). Lebt das alte Ziel noch, greift er es von dort erneut an; ist es
		// tot, bleibt er an der Position (Idle-Guard-Verhalten via AutoTarget).
		bool hasReturnPos;
		WPos returnPos;

		public AotReturnWhenEmpty(ActorInitializer init, AotReturnWhenEmptyInfo info)
		{
			this.info = info;
			lastAttackTarget = Target.Invalid;
		}

		void INotifyCreated.Created(Actor self)
		{
			ammoPools = self.TraitsImplementing<AmmoPool>().ToArray();
			armaments = self.TraitsImplementing<Armament>().ToArray();
		}

		// aotmod: Nur Ammo-Pools zaehlen, die eine AKTUELL aktivierte Waffe speisen (Waffe per
		// RequiresCondition enabled -- Pause zaehlt NICHT, denn genau die leere Waffe pausiert sich
		// per PauseOnCondition: !<ammo>). Sonst blockieren stillgelegte Pools die Leer-Erkennung:
		// der Firehawk (aot-firehawk) feuert nur die Rocket-Pool, traegt aber weiterhin die vollen
		// A-10-Pools vulcan/napalm -> ammoPools.All(!HasAmmo) wurde nie wahr -> kein Rearm-
		// Rueckflug, der Jet kreiste leer ueber dem Ziel.
		bool AllActivePoolsEmpty()
		{
			var sawActive = false;
			foreach (var p in ammoPools)
			{
				var isFed = false;
				foreach (var arm in armaments)
				{
					if (arm.IsTraitDisabled)
						continue;
					if (p.Info.Armaments.Contains(arm.Info.Name))
					{
						isFed = true;
						break;
					}
				}

				if (!isFed)
					continue;

				sawActive = true;
				if (p.HasAmmo)
					return false;
			}

			return sawActive;
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			lastAttackTarget = target;
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || self.IsDead)
				return;

			var isEmpty = AllActivePoolsEmpty();

			if (!wasEmpty && isEmpty && !hasReturnPos)
			{
				// Rueckkehr-Position merken (dort geht es nach dem Rearm wieder hin).
				returnPos = self.CenterPosition;
				hasReturnPos = true;

				if (lastAttackTarget.Type != TargetType.Invalid)
				{
					resumeTarget = lastAttackTarget;
					hasResumeTarget = true;
				}

				self.QueueActivity(false, new ReturnToBase(self, null, true));
			}

			wasEmpty = isEmpty;
		}

		// Ein manueller/neuer Befehl loest den automatisch gemerkten Angriff ab: schickt der Spieler
		// den Flieger unterwegs woandershin (Move/Stop/Idle), zum Aufmunitieren (ReturnToBase/Enter)
		// oder auf ein neues Ziel, darf er nach dem Rearm NICHT das alte Ziel wieder anfliegen.
		// Der automatische Rearm-Rueckflug selbst laeuft ueber QueueActivity, nicht ueber einen Order,
		// und wird davon daher nicht geloescht.
		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case "Move":
				case "Stop":
				case "Scatter":
				case "AttackMove":
				case "Attack":
				case "ForceAttack":
				case "Guard":
				case "ReturnToBase":
				case "Enter":
				case "Land":
					hasResumeTarget = false;
					resumeTarget = Target.Invalid;
					lastAttackTarget = Target.Invalid;
					hasReturnPos = false;
					break;
			}
		}

		void INotifyDockClient.Docked(Actor self, Actor dock) { }

		void INotifyDockClient.Undocked(Actor self, Actor dock)
		{
			// Zuerst zurueck an die Position, an der leergeschossen wurde (nicht am Pad haengen bleiben).
			if (hasReturnPos)
				self.QueueActivity(new Fly(self, Target.FromPos(returnPos)));

			// Lebt das alte Ziel noch, danach wieder angreifen; ist es tot, laeuft das FlyAttack ins
			// Leere und der Heli bleibt an der Position (AutoTarget uebernimmt neue Bedrohungen).
			if (hasResumeTarget)
				self.QueueActivity(new FlyAttack(self, AttackSource.Default, resumeTarget, true, Color.Red));

			hasResumeTarget = false;
			resumeTarget = Target.Invalid;
			lastAttackTarget = Target.Invalid;
			hasReturnPos = false;
		}
	}
}
