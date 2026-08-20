#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Automatically transforms the actor when it stands on specific terrain types,
 * OR when it stands adjacent to cells with AdjacentTerrainTypes.
 * Will NOT trigger if ExcludeIfAdjacentTo terrain is found nearby.
 * Supports RequiresCondition.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Automatically transform into another actor when standing on specific terrain types.")]
	public class AotTransformsOnTerrainInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor to transform into.")]
		public readonly string IntoActor = null;

		[FieldLoader.Require]
		[Desc("Terrain types that trigger the transformation.")]
		public readonly ImmutableArray<string> TerrainTypes = [];

		[Desc("Also trigger when standing on any terrain adjacent to cells with these types.")]
		public readonly ImmutableArray<string> AdjacentTerrainTypes = [];

		[Desc("Do NOT trigger if any adjacent cell has one of these terrain types.")]
		public readonly ImmutableArray<string> ExcludeIfAdjacentTo = [];

		[Desc("Skip the make animation when transforming.")]
		public readonly bool SkipMakeAnims = true;

		public override object Create(ActorInitializer init) { return new AotTransformsOnTerrain(init, this); }
	}

	public class AotTransformsOnTerrain : ConditionalTrait<AotTransformsOnTerrainInfo>, ITick, INotifyTransform
	{
		bool transformPending;

		// Set when the retrofit condition becomes available while this actor sits boarded in a
		// stationary garrison carrier (Fire Position). The actor is auto-ejected, transforms
		// once back in the world (Tick below), and is then sent straight back into this same
		// carrier (INotifyTransform.AfterTransform) so it visually "swaps in place" instead of
		// silently waiting for the player to pull it out manually. Null in every other case
		// (field retrofit, or boarded in a mobile transport - see TraitEnabled).
		Actor reboardTransport;

		public AotTransformsOnTerrain(ActorInitializer init, AotTransformsOnTerrainInfo info)
			: base(info) { }

		protected override void TraitEnabled(Actor self)
		{
			// Only auto-eject when the retrofit unlocks while boarded in a STATIONARY carrier
			// (the Fire Position). A Transform (full actor swap) cannot run on a Cargo passenger
			// - it is out of world, so Actor.Tick()/CurrentActivity never advances the queued
			// Transform. Without this the retrofit would silently stall until the player noticed
			// and manually unloaded the vehicle. Mobile carriers (transports/aircraft) are left
			// alone on purpose: ejecting a unit mid-ferry (e.g. over water) is worse than just
			// letting it transform when it is unloaded normally later.
			if (self.IsInWorld)
				return;

			var transport = self.TraitOrDefault<Passenger>()?.Transport;
			if (transport == null || transport.IsDead || !transport.IsInWorld)
				return;

			if (transport.TraitOrDefault<Mobile>() != null || transport.TraitOrDefault<Aircraft>() != null)
				return;

			reboardTransport = transport;
			transport.QueueActivity(new UnloadCargo(transport, WDist.Zero, unloadAll: false));
		}

		protected override void TraitDisabled(Actor self)
		{
			reboardTransport = null;
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
			{
				transformPending = false;
				return;
			}

			// A garrisoned/boarded actor (Cargo) is removed from the world - Actor.Tick()
			// (which drives CurrentActivity, i.e. the Transform activity queued below) then
			// stops running for it entirely, even though this ITick trait keeps ticking
			// regardless of world membership. Queuing a Transform here while out of world
			// would just leave it stuck forever (transformPending latched true, activity
			// never progresses). Skip while boarded and let the check re-run cleanly the
			// moment the actor is back in the world (next tick after AddedToWorld, e.g. after
			// the auto-eject queued in TraitEnabled has placed us next to the carrier).
			if (!self.IsInWorld)
			{
				transformPending = false;
				return;
			}

			var loc = self.Location;
			if (!self.World.Map.Contains(loc))
				return;

			var currentTerrain = self.World.Map.GetTerrainInfo(loc).Type;

			// Check exclusion: abort if any adjacent cell matches ExcludeIfAdjacentTo
			if (Info.ExcludeIfAdjacentTo.Length > 0)
			{
				foreach (var neighbor in CVec.Directions)
				{
					var adjacent = loc + neighbor;
					if (!self.World.Map.Contains(adjacent))
						continue;
					if (Info.ExcludeIfAdjacentTo.Contains(self.World.Map.GetTerrainInfo(adjacent).Type))
					{
						transformPending = false;
						return;
					}
				}
			}

			var triggered = Info.TerrainTypes.Contains(currentTerrain);

			if (!triggered && Info.AdjacentTerrainTypes.Length > 0)
			{
				foreach (var neighbor in CVec.Directions)
				{
					var adjacent = loc + neighbor;
					if (!self.World.Map.Contains(adjacent))
						continue;
					if (Info.AdjacentTerrainTypes.Contains(self.World.Map.GetTerrainInfo(adjacent).Type))
					{
						triggered = true;
						break;
					}
				}
			}

			if (!triggered)
			{
				transformPending = false;
				return;
			}

			if (transformPending)
				return;

			transformPending = true;

			var transform = new Transform(Info.IntoActor)
			{
				SkipMakeAnims = Info.SkipMakeAnims,
				Facing = self.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero
			};

			self.CancelActivity();
			self.QueueActivity(false, transform);
		}

		void INotifyTransform.BeforeTransform(Actor self) { }
		void INotifyTransform.OnTransform(Actor self) { }

		void INotifyTransform.AfterTransform(Actor toActor)
		{
			// Send the freshly transformed actor straight back into the carrier it was
			// auto-ejected from (see TraitEnabled). Mirrors Transform's own
			// IssueOrderAfterTransform path (that type is internal to Mods.Common, so we issue
			// the EnterTransport order to the new actor directly here instead). toActor is
			// already in the world at this point (World.CreateActor added it before this hook).
			if (reboardTransport == null)
				return;

			if (!reboardTransport.IsDead && reboardTransport.IsInWorld)
			{
				var order = new Order("EnterTransport", toActor, Target.FromActor(reboardTransport), true);
				foreach (var t in toActor.TraitsImplementing<IResolveOrder>())
					t.ResolveOrder(toActor, order);
			}

			reboardTransport = null;
		}
	}
}
