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

using System.Collections.Generic;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class AotAntTypeInit : ValueActorInit<string>, ISingleInstanceInit
	{
		public AotAntTypeInit(TraitInfo info, string value) : base(info, value) { }
	}

	public class AotAntMaxCountInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotAntMaxCountInit(TraitInfo info, int value) : base(info, value) { }
	}

	public class AotAntPatrolRadiusInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotAntPatrolRadiusInit(TraitInfo info, int value) : base(info, value) { }

		// Wird vom Nest an die gespawnte Ameise weitergereicht (dort haengt kein AotAntSpawnerInfo dran).
		public AotAntPatrolRadiusInit(int value) : base(value) { }
	}

	[Desc("aotmod: Unsichtbarer Nest-Aktor. Spawnt Ameisen-Critter nach.",
		"Typ, Anzahl und Patrouillienradius sind im Map-Editor einstellbar.",
		"Das Patrouillieren selbst macht AotAntWander auf der Ameise.")]
	public class AotAntSpawnerInfo : TraitInfo, IEditorActorOptions
	{
		[Desc("Aktor-IDs der Ameisen-Varianten (für 'random'-Modus).")]
		public readonly string[] AntActors = ["aot-ant", "aot-fireant", "aot-scoutant"];

		[Desc("Standard-Ameisentyp.")]
		public readonly string DefaultAntType = "random";

		[Desc("Standard-Maximalanzahl gleichzeitiger Ameisen.")]
		public readonly int DefaultMaxAnts = 3;

		[Desc("Standard-Patrouillienradius in Zellen (1-10).")]
		public readonly int DefaultPatrolRadius = 5;

		[Desc("Ticks zwischen Spawn-Versuchen.")]
		public readonly int SpawnInterval = 150;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			var antLabels = new Dictionary<string, string>
			{
				{ "random", "Random" },
				{ "aot-ant", "Worker Ant" },
				{ "aot-fireant", "Fire Ant" },
				{ "aot-scoutant", "Scout Ant" },
			};

			yield return new EditorActorDropdown("Ant Type", 0,
				_ => antLabels,
				(actor, _) => actor.GetInitOrDefault<AotAntTypeInit>(this)?.Value ?? DefaultAntType,
				(actor, value) => actor.ReplaceInit(new AotAntTypeInit(this, value), this));

			yield return new EditorActorSlider("Max Ants", 1, 1, 10, 9,
				actor => actor.GetInitOrDefault<AotAntMaxCountInit>(this)?.Value ?? DefaultMaxAnts,
				(actor, value) => actor.ReplaceInit(new AotAntMaxCountInit(this, (int)value), this));

			yield return new EditorActorSlider("Patrol Radius", 2, 1, 10, 9,
				actor => actor.GetInitOrDefault<AotAntPatrolRadiusInit>(this)?.Value ?? DefaultPatrolRadius,
				(actor, value) => actor.ReplaceInit(new AotAntPatrolRadiusInit(this, (int)value), this));
		}

		public override object Create(ActorInitializer init) { return new AotAntSpawner(init, this); }
	}

	public class AotAntSpawner : ITick
	{
		readonly AotAntSpawnerInfo info;
		readonly string antType;
		readonly int maxAnts;
		readonly int patrolRadius;
		readonly List<Actor> spawnedAnts = [];
		int spawnTick;

		public AotAntSpawner(ActorInitializer init, AotAntSpawnerInfo info)
		{
			this.info = info;
			antType = init.GetValue<AotAntTypeInit, string>(info.DefaultAntType);
			maxAnts = init.GetValue<AotAntMaxCountInit, int>(info.DefaultMaxAnts);
			patrolRadius = init.GetValue<AotAntPatrolRadiusInit, int>(info.DefaultPatrolRadius);
			spawnTick = info.SpawnInterval;
		}

		void ITick.Tick(Actor self)
		{
			spawnedAnts.RemoveAll(a => a.IsDead || !a.IsInWorld);

			if (spawnedAnts.Count >= maxAnts)
				return;

			if (--spawnTick > 0)
				return;

			spawnTick = info.SpawnInterval;
			SpawnAnt(self);
		}

		void SpawnAnt(Actor self)
		{
			var type = antType == "random"
				? info.AntActors[self.World.SharedRandom.Next(info.AntActors.Length)]
				: antType;

			if (!self.World.Map.Rules.Actors.ContainsKey(type))
				return;

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				var ant = w.CreateActor(type, new TypeDictionary
				{
					new LocationInit(self.Location),
					new OwnerInit(self.Owner),
					new AotAntPatrolRadiusInit(patrolRadius),
				});

				spawnedAnts.Add(ant);
			});
		}
	}
}
