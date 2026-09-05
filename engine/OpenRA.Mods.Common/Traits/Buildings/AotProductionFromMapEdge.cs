#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Red-Alert-style aircraft delivery. A produced aircraft does NOT pop into existence on the
 * production building's pad (which, for the four-pad helipad, made freshly-built helis crowd and
 * nudge each other off their pads). Instead the aircraft itself spawns at the closest map edge,
 * at cruise altitude, and flies IN to the building -- more immersive and, because they arrive one
 * by one along a flight path, they settle onto distinct pads cleanly.
 *
 *   - No rally point: the aircraft flies to the building and docks on the next free landing pad
 *     (ReturnToBase reserves the first free Reservable slot, exactly like a returning aircraft).
 *   - Rally point set: the aircraft flies to the building FIRST and then continues to the rally
 *     point (so the fly-in reads correctly and the player's rally is still honoured).
 *
 * Non-aircraft producees fall back to the normal exit-based Production.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: produced aircraft spawn at the closest map edge and fly in to the building",
		"(RA skylift feel). Without a rally point they dock on the next free pad; with a rally point",
		"they fly to the building first and then on to the rally point. See AotProductionFromMapEdge.cs.")]
	sealed class AotProductionFromMapEdgeInfo : ProductionInfo
	{
		public override object Create(ActorInitializer init) { return new AotProductionFromMapEdge(init, this); }
	}

	sealed class AotProductionFromMapEdge : Production
	{
		RallyPoint rp;

		public AotProductionFromMapEdge(ActorInitializer init, ProductionInfo info)
			: base(init, info) { }

		protected override void Created(Actor self)
		{
			base.Created(self);
			rp = self.TraitOrDefault<RallyPoint>();
		}

		public override bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return false;

			var aircraftInfo = producee.TraitInfoOrDefault<AircraftInfo>();

			// Ground/other producees keep the normal exit-based spawn.
			if (aircraftInfo == null)
				return base.Produce(self, producee, productionType, inits, refundableValue);

			var map = self.World.Map;
			var hasRally = rp != null && rp.Path.Count > 0;
			var firstDest = hasRally ? rp.Path[0] : self.Location;

			var edge = map.ChooseClosestEdgeCell(self.Location);
			var spawnPos = map.CenterOfCell(edge) + new WVec(0, 0, aircraftInfo.CruiseAltitude.Length);
			var initialFacing = map.FacingBetween(edge, self.Location, WAngle.Zero);

			self.World.AddFrameEndTask(w =>
			{
				if (!self.IsInWorld || self.IsDead)
				{
					self.Owner.PlayerActor.Trait<PlayerResources>().RefundCash(refundableValue);
					return;
				}

				var td = new TypeDictionary(inits)
				{
					new LocationInit(edge),
					new CenterPositionInit(spawnPos),
					new FacingInit(initialFacing)
				};

				var newUnit = w.CreateActor(producee.Name, td);

				if (hasRally)
				{
					// Fly to the building first, then continue to the rally point.
					var move = newUnit.TraitOrDefault<IMove>();
					if (move != null)
					{
						newUnit.QueueActivity(move.MoveTo(self.Location, 2, evaluateNearestMovableCell: true));
						foreach (var cell in rp.Path)
							newUnit.QueueActivity(move.MoveTo(cell, 2, evaluateNearestMovableCell: true));
					}
				}
				else
				{
					// Fly in and dock on the next free landing pad of this building.
					newUnit.QueueActivity(new ReturnToBase(newUnit, self, true));
				}

				if (!self.IsDead)
					foreach (var t in self.TraitsImplementing<INotifyProduction>())
						t.UnitProduced(self, newUnit, firstDest);

				foreach (var notify in self.World.ActorsWithTrait<INotifyOtherProduction>())
					notify.Trait.UnitProducedByOther(notify.Actor, self, newUnit, productionType, td);
			});

			return true;
		}
	}
}
