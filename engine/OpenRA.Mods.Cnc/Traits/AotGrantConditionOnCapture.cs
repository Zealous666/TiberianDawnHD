#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Grants a condition when this actor is captured by a specific capture type.")]
	public class AotGrantConditionOnCaptureInfo : TraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Condition to grant when captured.")]
		public readonly string Condition = null;

		[Desc("Only grant the condition when captured by one of these capture types.")]
		public readonly BitSet<CaptureType> CaptureTypes = default;

		public override object Create(ActorInitializer init) { return new AotGrantConditionOnCapture(this); }
	}

	public class AotGrantConditionOnCapture : INotifyCapture
	{
		readonly AotGrantConditionOnCaptureInfo info;
		int conditionToken = Actor.InvalidConditionToken;

		public AotGrantConditionOnCapture(AotGrantConditionOnCaptureInfo info)
		{
			this.info = info;
		}

		void INotifyCapture.OnCapture(Actor self, Actor captor, Player oldOwner, Player newOwner, BitSet<CaptureType> captureTypes)
		{
			if (info.CaptureTypes.IsEmpty || captureTypes.Overlaps(info.CaptureTypes))
				if (conditionToken == Actor.InvalidConditionToken)
					conditionToken = self.GrantCondition(info.Condition);
		}
	}

	[Desc("Spawns an actor for the capturing player when this actor is killed after being captured by a specific capture type.")]
	public class AotSpawnCarjackerOnCaptureDeathInfo : TraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor to spawn on death after being captured.")]
		public readonly string Actor = null;

		[Desc("Only trigger when captured by one of these capture types.")]
		public readonly BitSet<CaptureType> CaptureTypes = default;

		public override object Create(ActorInitializer init) { return new AotSpawnCarjackerOnCaptureDeath(this); }
	}

	public class AotSpawnCarjackerOnCaptureDeath : INotifyCapture, INotifyKilled
	{
		readonly AotSpawnCarjackerOnCaptureDeathInfo info;
		Player capturingPlayer;

		public AotSpawnCarjackerOnCaptureDeath(AotSpawnCarjackerOnCaptureDeathInfo info)
		{
			this.info = info;
		}

		void INotifyCapture.OnCapture(Actor self, Actor captor, Player oldOwner, Player newOwner, BitSet<CaptureType> captureTypes)
		{
			if (info.CaptureTypes.IsEmpty || captureTypes.Overlaps(info.CaptureTypes))
				capturingPlayer = newOwner;
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (capturingPlayer == null || !self.IsInWorld)
				return;

			self.World.AddFrameEndTask(w =>
			{
				w.CreateActor(info.Actor, new TypeDictionary
				{
					new OwnerInit(capturingPlayer),
					new LocationInit(self.Location),
				});
			});
		}
	}
}
