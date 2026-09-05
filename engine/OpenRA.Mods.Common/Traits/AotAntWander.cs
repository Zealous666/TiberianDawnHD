#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Patrol behaviour for ant critters. Like AttackWander, but the radius is supplied
 * per-instance at spawn time by the spawner (AotCritterSpawner editor slider), and the
 * patrol is anchored to the nest cell instead of drifting with the actor.
 */
#endregion

using System.Collections.Frozen;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Patrouilliert im Umkreis des Nests und greift dabei alles an.",
		"Wie AttackWander, aber der Radius kommt zur Spawn-Zeit vom Nest (Editor-Regler).",
		"Laeuft NUR im Idle -> unterbricht niemals einen laufenden Angriff.")]
	public class AotAntWanderInfo : ConditionalTraitInfo, Requires<IMoveInfo>, Requires<AttackMoveInfo>
	{
		[Desc("Radius in Zellen, falls kein Nest einen mitgibt (z.B. im Editor platzierte Ameisen).")]
		public readonly int DefaultPatrolRadius = 5;

		[Desc("Minimale Wartezeit in Ticks vor dem naechsten Patrouillengang.")]
		public readonly int MinMoveDelay = 25;

		[Desc("Maximale Wartezeit in Ticks vor dem naechsten Patrouillengang.")]
		public readonly int MaxMoveDelay = 100;

		[Desc("Terraintypen, die beim Patrouillieren gemieden werden.")]
		public readonly FrozenSet<string> AvoidTerrainTypes = FrozenSet<string>.Empty;

		public override object Create(ActorInitializer init) { return new AotAntWander(init, this); }
	}

	public class AotAntWander : ConditionalTrait<AotAntWanderInfo>, INotifyIdle, INotifyBecomingIdle
	{
		readonly int patrolRadius;
		readonly IMove move;
		readonly AttackMoveInfo attackMoveInfo;

		CPos home;
		int countdown;

		public AotAntWander(ActorInitializer init, AotAntWanderInfo info)
			: base(info)
		{
			patrolRadius = init.GetValue<AotAntPatrolRadiusInit, int>(info.DefaultPatrolRadius);
			move = init.Self.Trait<IMove>();
			attackMoveInfo = init.Self.Info.TraitInfo<AttackMoveInfo>();
		}

		protected override void Created(Actor self)
		{
			// Die Ameise wird auf der Nest-Zelle erzeugt -> das ist ihr Patrouillen-Mittelpunkt.
			home = self.Location;
			countdown = self.World.SharedRandom.Next(Info.MinMoveDelay, Info.MaxMoveDelay);

			base.Created(self);
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			countdown = self.World.SharedRandom.Next(Info.MinMoveDelay, Info.MaxMoveDelay);
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (IsTraitDisabled)
				return;

			if (--countdown > 0)
				return;

			countdown = self.World.SharedRandom.Next(Info.MinMoveDelay, Info.MaxMoveDelay);

			var targetCell = PickPatrolCell(self);
			if (targetCell == null)
				return;

			// AttackMoveActivity (nicht Move.MoveTo): die Ameise greift unterwegs alles an,
			// was in Reichweite kommt, statt stur zum Ziel zu laufen.
			self.QueueActivity(new AttackMoveActivity(self,
				() => move.MoveTo(targetCell.Value, 2, targetLineColor: attackMoveInfo.TargetLineColor)));
		}

		CPos? PickPatrolCell(Actor self)
		{
			var map = self.World.Map;
			var cell = home + new CVec(
				self.World.SharedRandom.Next(-patrolRadius, patrolRadius + 1),
				self.World.SharedRandom.Next(-patrolRadius, patrolRadius + 1));

			if (!map.Contains(cell))
				return null;

			if (Info.AvoidTerrainTypes.Count > 0 && Info.AvoidTerrainTypes.Contains(map.GetTerrainInfo(cell).Type))
				return null;

			return cell;
		}
	}
}
