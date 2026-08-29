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

using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Replace with another actor when a resource spawns adjacent.")]
	public class TransformsNearResourcesInfo : TraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		public readonly string IntoActor = null;

		public readonly CVec Offset = CVec.Zero;

		[Desc("Don't render the make animation.")]
		public readonly bool SkipMakeAnims = false;

		[FieldLoader.Require]
		[Desc("Resource type which triggers the transformation.")]
		public readonly string Type = null;

		[Desc("Resource density threshold which is required.")]
		public readonly byte Density = 1;

		[Desc("This many adjacent resource tiles are required.")]
		public readonly int Adjacency = 1;

		[Desc("The range of time (in ticks) until the transformation starts.")]
		public readonly ImmutableArray<int> Delay = [1000, 3000];

		[Desc("aotmod: Sobald IRGENDEIN Spieler dieses Prerequisite besitzt (via world-trait",
			"AotGlobalPrerequisiteFlag), wird der Delay-Timer ignoriert und -- solange die",
			"Resource-Adjazenz erfuellt ist -- sofort transformiert. Deckelt die Wartezeit gegen",
			"'spaetestens wenn der erste Spieler dieses Age erreicht'. Leer = kein Cap.")]
		public readonly string ForcePrerequisite = null;

		public override object Create(ActorInitializer init) { return new TransformsNearResources(init.Self, this); }
	}

	public class TransformsNearResources : ITick
	{
		readonly TransformsNearResourcesInfo info;
		readonly IResourceLayer resourceLayer;
		readonly AotGlobalPrerequisiteFlag forceFlag;
		int delay;

		public TransformsNearResources(Actor self, TransformsNearResourcesInfo info)
		{
			resourceLayer = self.World.WorldActor.Trait<IResourceLayer>();
			delay = Common.Util.RandomInRange(self.World.SharedRandom, info.Delay);
			this.info = info;

			if (!string.IsNullOrEmpty(info.ForcePrerequisite))
				forceFlag = self.World.WorldActor.TraitsImplementing<AotGlobalPrerequisiteFlag>()
					.FirstOrDefault(f => f.Info.Prerequisite == info.ForcePrerequisite);
		}

		void ITick.Tick(Actor self)
		{
			if (delay < 0)
				return;

			var adjacent = 0;
			foreach (var direction in CVec.Directions)
			{
				var location = self.Location + direction;

				var resource = resourceLayer.GetResource(location);
				if (resource.Type == null || resource.Type != info.Type)
					continue;

				if (resource.Density < info.Density)
					continue;

				if (++adjacent < info.Adjacency)
					continue;

				// aotmod: hat der erste Spieler das Age erreicht, sofort transformieren
				// (Adjazenz ist hier ja bereits erfuellt), sonst normal herunterzaehlen.
				if (forceFlag != null && forceFlag.Reached)
					delay = -1;
				else
					delay--;
				break;
			}

			if (delay < 0)
				Transform(self);
		}

		void Transform(Actor self)
		{
			var transform = new Transform(info.IntoActor);

			var facing = self.TraitOrDefault<IFacing>();
			if (facing != null)
				transform.Facing = facing.Facing;

			transform.SkipMakeAnims = info.SkipMakeAnims;
			transform.Offset = info.Offset;

			self.QueueActivity(false, transform);
		}
	}
}
