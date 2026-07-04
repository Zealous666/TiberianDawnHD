#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Intercepts the DeployTransform order, grants an "undeploying" condition for a
 * configurable number of ticks (so WithMakeAnimation can play a reverse animation),
 * then triggers the actual Transform activity.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Plays an undeploying condition for UndeployDuration ticks before transforming into another actor.")]
	public class AotDelayedTransformsInfo : TraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor to transform into after the undeploy animation.")]
		public readonly string IntoActor = null;

		[Desc("Offset to spawn the transformed actor relative to the current cell.")]
		public readonly CVec Offset = CVec.Zero;

		[Desc("Facing of the spawned actor.")]
		public readonly WAngle Facing = new(384);

		[Desc("Sounds to play when the undeploy order is issued.")]
		public readonly string[] TransformSounds = [];

		[GrantedConditionReference]
		[Desc("Condition granted while the undeploy animation plays.")]
		public readonly string UndeployCondition = null;

		[Desc("Number of ticks to wait before transforming (should match animation length).")]
		public readonly int UndeployDuration = 24;

		[CursorReference]
		[Desc("Cursor to display when able to undeploy.")]
		public readonly string DeployCursor = "deploy";

		public override object Create(ActorInitializer init) { return new AotDelayedTransforms(init, this); }
	}

	public class AotDelayedTransforms : IIssueOrder, IResolveOrder, IIssueDeployOrder, ITick
	{
		readonly Actor self;
		readonly AotDelayedTransformsInfo info;
		readonly string faction;

		int conditionToken = Actor.InvalidConditionToken;
		int countdown = -1;

		public AotDelayedTransforms(ActorInitializer init, AotDelayedTransformsInfo info)
		{
			self = init.Self;
			this.info = info;
			faction = init.GetValue<FactionInit, string>(self.Owner.Faction.InternalName);
		}

		public IEnumerable<IOrderTargeter> Orders
		{
			get
			{
				if (countdown < 0)
					yield return new DeployOrderTargeter("DeployTransform", 5, () => info.DeployCursor);
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID == "DeployTransform")
				return new Order(order.OrderID, self, queued);
			return null;
		}

		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued)
		{
			return new Order("DeployTransform", self, queued);
		}

		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued) { return countdown < 0; }

		public void ResolveOrder(Actor self, Order order)
		{
			System.Console.Error.WriteLine($"[AotDelayedTransforms] ResolveOrder: {order.OrderString}, countdown={countdown}");
			if (order.OrderString != "DeployTransform" || countdown >= 0)
				return;

			foreach (var s in info.TransformSounds)
				Game.Sound.PlayToPlayer(SoundType.World, self.Owner, s);

			if (!string.IsNullOrEmpty(info.UndeployCondition))
				conditionToken = self.GrantCondition(info.UndeployCondition);

			countdown = info.UndeployDuration;
			System.Console.Error.WriteLine($"[AotDelayedTransforms] countdown set to {countdown}, condition={info.UndeployCondition}");
		}

		public void Tick(Actor self)
		{
			if (countdown < 0)
				return;

			if (--countdown > 0)
				return;

			countdown = -1;

			if (conditionToken != Actor.InvalidConditionToken)
			{
				self.RevokeCondition(conditionToken);
				conditionToken = Actor.InvalidConditionToken;
			}

			self.QueueActivity(false, new Transform(info.IntoActor)
			{
				Offset = info.Offset,
				Facing = info.Facing,
				Faction = faction,
				SkipMakeAnims = true
			});
		}
	}
}
