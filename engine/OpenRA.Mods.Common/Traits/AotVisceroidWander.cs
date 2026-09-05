#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Wander behaviour for Visceroids spawned by the vein heart. Like AttackWander, but the
 * radius is supplied per-instance at spawn time by the spawner (AotCritterSpawner editor
 * slider), the same pattern as AotAntWander for the ant nest.
 */
#endregion

using System.Collections.Frozen;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class AotVisceroidWanderRadiusInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotVisceroidWanderRadiusInit(TraitInfo info, int value) : base(info, value) { }

		// Wird vom Heart an den gespawnten Visceroid weitergereicht (dort haengt kein
		// AotCritterSpawnerInfo dran).
		public AotVisceroidWanderRadiusInit(int value) : base(value) { }
	}

	[Desc("aotmod: Wandert im Umkreis des Spawn-Punkts und greift dabei alles an.",
		"Wie AttackWander, aber der Radius kommt zur Spawn-Zeit vom Vein-Heart (Editor-Regler).",
		"Laeuft NUR im Idle -> unterbricht niemals einen laufenden Angriff.")]
	public class AotVisceroidWanderInfo : ConditionalTraitInfo, Requires<IMoveInfo>, Requires<AttackMoveInfo>
	{
		[Desc("Radius in Zellen, falls kein Heart einen mitgibt (z.B. im Editor platzierte Visceroids).")]
		public readonly int DefaultWanderRadius = 8;

		[Desc("Minimale Wartezeit in Ticks vor dem naechsten Wander-Schritt.")]
		public readonly int MinMoveDelay = 25;

		[Desc("Maximale Wartezeit in Ticks vor dem naechsten Wander-Schritt.")]
		public readonly int MaxMoveDelay = 90;

		[Desc("Terraintypen, die beim Wandern gemieden werden.")]
		public readonly FrozenSet<string> AvoidTerrainTypes = FrozenSet<string>.Empty;

		public override object Create(ActorInitializer init) { return new AotVisceroidWander(init, this); }
	}

	public class AotVisceroidWander : ConditionalTrait<AotVisceroidWanderInfo>, INotifyIdle, INotifyBecomingIdle
	{
		readonly int wanderRadius;
		readonly IMove move;
		readonly AttackMoveInfo attackMoveInfo;

		CPos home;
		int countdown;

		public AotVisceroidWander(ActorInitializer init, AotVisceroidWanderInfo info)
			: base(info)
		{
			wanderRadius = init.GetValue<AotVisceroidWanderRadiusInit, int>(info.DefaultWanderRadius);
			move = init.Self.Trait<IMove>();
			attackMoveInfo = init.Self.Info.TraitInfo<AttackMoveInfo>();
		}

		protected override void Created(Actor self)
		{
			// Der Visceroid wird ausserhalb des Heart-Footprints erzeugt -> das ist sein
			// Wander-Mittelpunkt.
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

			var targetCell = PickWanderCell(self);
			if (targetCell == null)
				return;

			self.QueueActivity(new AttackMoveActivity(self,
				() => move.MoveTo(targetCell.Value, 2, targetLineColor: attackMoveInfo.TargetLineColor)));
		}

		CPos? PickWanderCell(Actor self)
		{
			var map = self.World.Map;
			var cell = home + new CVec(
				self.World.SharedRandom.Next(-wanderRadius, wanderRadius + 1),
				self.World.SharedRandom.Next(-wanderRadius, wanderRadius + 1));

			if (!map.Contains(cell))
				return null;

			if (Info.AvoidTerrainTypes.Count > 0 && Info.AvoidTerrainTypes.Contains(map.GetTerrainInfo(cell).Type))
				return null;

			return cell;
		}
	}
}
