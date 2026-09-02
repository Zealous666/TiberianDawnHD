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
	// aotmod (2026-07-29): Die Init-Klassen heissen weiter "AotAnt*", obwohl der Spawner jetzt alle
	// Critter abdeckt. Absicht: die Namen landen als Schluessel in den Karten (z.B.
	// "AotAntMaxCount: 5" in aot_forestfires) -- ein Rename wuerde jede bestehende Karte still
	// kaputtmachen. AotAntPatrolRadiusInit wird ausserdem von AotAntWander konsumiert.
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

		// Wird vom Spawner an den gespawnten Critter weitergereicht (dort haengt kein Spawner-Info dran).
		public AotAntPatrolRadiusInit(int value) : base(value) { }
	}

	[Desc("aotmod: Unsichtbarer Spawner-Aktor. Spawnt Critter nach -- Ameisen, Visceroid oder",
		"Dinosaurier. Typ, Anzahl und Patrouillienradius sind im Map-Editor einstellbar.",
		"Das Patrouillieren selbst macht AotAntWander bzw. AotVisceroidWander auf dem Critter.")]
	public class AotCritterSpawnerInfo : TraitInfo, IEditorActorOptions
	{
		[Desc("Aktor-IDs fuer den Modus \"Random Ants\".")]
		public readonly string[] AntActors = ["aot-ant", "aot-fireant", "aot-scoutant"];

		[Desc("Aktor-IDs fuer den Modus \"Random Dinosaurs\".")]
		public readonly string[] DinoActors = ["aot-steg", "aot-trex", "aot-tric", "aot-rapt"];

		[Desc("Standard-Crittertyp: eine Aktor-ID, \"random-ants\" oder \"random-dinos\".")]
		public readonly string DefaultCritterType = "random-ants";

		[Desc("Standard-Maximalanzahl gleichzeitig lebender Critter.")]
		public readonly int DefaultMaxCritters = 3;

		[Desc("Standard-Patrouillienradius in Zellen (1-10).")]
		public readonly int DefaultPatrolRadius = 5;

		[Desc("Ticks zwischen Spawn-Versuchen.")]
		public readonly int SpawnInterval = 150;

		[Desc("Critter unten aus dem Footprint entlassen (z.B. Hoehleneingang am unteren Rand).",
			"Default: zufaellige freie Ring-Zelle. Faellt darauf zurueck, wenn unten alles blockiert ist.")]
		public readonly bool PreferBottomExit = false;

		[Desc("CHECKBOX \"Attack Wave\": gespawnte Critter patrouillieren NICHT, sondern suchen sich",
			"ein zufaelliges Feindziel (bevorzugt eine Basis) und marschieren angreifend dorthin.",
			"Siehe AotCritterAttackWave auf dem Critter.")]
		public readonly bool DefaultAttackWave = false;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			var labels = new Dictionary<string, string>
			{
				{ "random-ants", "Random Ants" },
				{ "random-dinos", "Random Dinosaurs" },
				{ "aot-ant", "Worker Ant" },
				{ "aot-fireant", "Fire Ant" },
				{ "aot-scoutant", "Scout Ant" },
				{ "aot-visceroid", "Visceroid" },
				{ "aot-steg", "Stegosaurus" },
				{ "aot-trex", "Tyrannosaurus Rex" },
				{ "aot-tric", "Triceratops" },
				{ "aot-rapt", "Velociraptor" },
			};

			yield return new EditorActorDropdown("Critter Type", 0,
				_ => labels,
				(actor, _) => Normalise(actor.GetInitOrDefault<AotAntTypeInit>(this)?.Value ?? DefaultCritterType),
				(actor, value) => actor.ReplaceInit(new AotAntTypeInit(this, value), this));

			yield return new EditorActorSlider("Max Critters", 1, 1, 10, 9,
				actor => actor.GetInitOrDefault<AotAntMaxCountInit>(this)?.Value ?? DefaultMaxCritters,
				(actor, value) => actor.ReplaceInit(new AotAntMaxCountInit(this, (int)value), this));

			yield return new EditorActorSlider("Patrol Radius", 2, 1, 10, 9,
				actor => actor.GetInitOrDefault<AotAntPatrolRadiusInit>(this)?.Value ?? DefaultPatrolRadius,
				(actor, value) => actor.ReplaceInit(new AotAntPatrolRadiusInit(this, (int)value), this));

			// Schliesst den Patrol-Radius faktisch aus (der Critter patrouilliert dann nicht mehr),
			// bleibt aber bewusst getrennt einstellbar: so kann man zwischen beiden Modi
			// umschalten, ohne den eingestellten Radius zu verlieren.
			yield return new EditorActorCheckbox("Attack Wave", 3,
				actor => actor.GetInitOrDefault<AotCritterAttackWaveInit>(this)?.Value ?? DefaultAttackWave,
				(actor, value) => actor.ReplaceInit(new AotCritterAttackWaveInit(this, value), this));
		}

		// Karten, die vor der Critter-Erweiterung gespeichert wurden, tragen noch "random"
		// (= damals immer Ameisen). Auf den neuen Schluessel abbilden, sonst faende der
		// Editor-Dropdown keinen passenden Eintrag und die Karte spawnte nichts mehr.
		public static string Normalise(string type) => type == "random" ? "random-ants" : type;

		public override object Create(ActorInitializer init) { return new AotCritterSpawner(init, this); }
	}

	public class AotCritterSpawner : ITick
	{
		readonly AotCritterSpawnerInfo info;
		readonly string critterType;
		readonly int maxCritters;
		readonly int patrolRadius;
		readonly bool attackWave;
		readonly List<Actor> spawned = [];
		int spawnTick;

		public AotCritterSpawner(ActorInitializer init, AotCritterSpawnerInfo info)
		{
			this.info = info;
			critterType = AotCritterSpawnerInfo.Normalise(init.GetValue<AotAntTypeInit, string>(info.DefaultCritterType));
			maxCritters = init.GetValue<AotAntMaxCountInit, int>(info.DefaultMaxCritters);
			patrolRadius = init.GetValue<AotAntPatrolRadiusInit, int>(info.DefaultPatrolRadius);
			attackWave = init.GetValue<AotCritterAttackWaveInit, bool>(info.DefaultAttackWave);
			spawnTick = info.SpawnInterval;
		}

		void ITick.Tick(Actor self)
		{
			spawned.RemoveAll(a => a.IsDead || !a.IsInWorld);

			if (maxCritters <= 0 || spawned.Count >= maxCritters)
				return;

			if (--spawnTick > 0)
				return;

			spawnTick = info.SpawnInterval;
			Spawn(self);
		}

		void Spawn(Actor self)
		{
			var type = critterType switch
			{
				"random-ants" => Pick(self, info.AntActors),
				"random-dinos" => Pick(self, info.DinoActors),
				_ => critterType,
			};

			if (type == null || !self.World.Map.Rules.Actors.TryGetValue(type, out var critterInfo))
				return;

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				// Spawn-Zelle MUSS ausserhalb des eigenen Footprints liegen: haengt der Spawner an
				// einem soliden Gebaeude (Vein-Heart 3x3, Ant-Bau 4x3), kaeme der Critter sonst auf
				// einer blockierten Zelle zur Welt und koennte nicht herauslaufen, bis das Gebaeude
				// stirbt (Locomotor behandelt ein stationaeres Building als uncrushbaren Blocker).
				// Beim unsichtbaren Editor-Marker (OccupiesSpace:false) faellt der Helper auf
				// self.Location zurueck -> unveraendertes Verhalten.
				var spawnCell = AotFootprintUtils.FindCellOutsideFootprint(self, info.PreferBottomExit);

				var inits = new TypeDictionary
				{
					new LocationInit(spawnCell),
					new OwnerInit(self.Owner),
				};

				// Welcher Radius-Init passt, entscheidet der Critter selbst: Visceroids nutzen
				// AotVisceroidWander, Ameisen und Dinos AotAntWander. Per Trait-Lookup statt
				// harter Typliste -- so wirkt der Editor-Regler automatisch auch fuer kuenftige
				// Critter, ohne dass hier etwas nachgezogen werden muss.
				if (critterInfo.HasTraitInfo<AotVisceroidWanderInfo>())
					inits.Add(new AotVisceroidWanderRadiusInit(patrolRadius));
				else if (critterInfo.HasTraitInfo<AotAntWanderInfo>())
					inits.Add(new AotAntPatrolRadiusInit(patrolRadius));

				// Wander-Condition mitgeben: einzeln im Editor platzierte Critter bleiben per Default
				// STEHEN (Checkbox "Wander Around" aus), aber ein Spawner soll patrouillierende Critter
				// erzeugen -- also die Condition beim Spawn setzen. Bei aktiver Angriffswelle unnoetig
				// (die Welle schaltet das Wandern ohnehin ab), aber harmlos.
				if (critterInfo.HasTraitInfo<AotWanderAroundInfo>())
					inits.Add(new AotWanderAroundInit(true));

				// Nur setzen wenn aktiv: der Critter-Default (DefaultAttackWave) soll sonst gelten,
				// damit im Editor direkt platzierte Critter unabhaengig vom Spawner bleiben.
				if (attackWave && critterInfo.HasTraitInfo<AotCritterAttackWaveInfo>())
					inits.Add(new AotCritterAttackWaveInit(true));

				spawned.Add(w.CreateActor(type, inits));
			});
		}

		static string Pick(Actor self, string[] pool)
		{
			return pool.Length == 0 ? null : pool[self.World.SharedRandom.Next(pool.Length)];
		}
	}
}
