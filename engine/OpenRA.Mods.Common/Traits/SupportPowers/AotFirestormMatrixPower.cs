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
using System.Linq;
using OpenRA.Mods.Common.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Firestorm Defense Matrix (TS-Portierung). Aktiviert fuer ActiveDuration Ticks eine",
		"Energiebarriere auf allen eigenen Wall-Aktoren (WallActors): Flugeinheiten, die eine",
		"Wall-Zelle ueberfliegen, werden zerstoert; NukeLaunch-Projektile (Atom Bomb, Tiberium",
		"Strike, Mine Cluster) werden abgefangen — direkt ueber der Wand ODER beim Sinkflug auf",
		"ein von Matrix-Zellen umschlossenes Gebiet (Einschluss-BFS, da NukeLaunch nicht",
		"horizontal fliegt). Verteilt waehrend der Aktivierung WallCondition (ExternalCondition)",
		"an alle Segmente fuer die Firestorm-Optik und ActiveCondition an den Traeger selbst.")]
	public class AotFirestormMatrixPowerInfo : SupportPowerInfo
	{
		[Desc("Dauer der Aktivierung in Ticks.")]
		public readonly int ActiveDuration = 625;

		[Desc("Actor-Typen, die als Matrix-Wandsegmente zaehlen (nur eigene).")]
		public readonly string[] WallActors = ["aot-wall-gdi", "aot-wall-gdi-fs"];

		[Desc("ExternalCondition, die waehrend der Aktivierung auf alle Wandsegmente verteilt wird.")]
		public readonly string WallCondition = "aot-fs-matrix";

		[GrantedConditionReference]
		[Desc("Condition auf dem Traeger-Aktor waehrend die Matrix aktiv ist (Flame-Overlay/Ambient-Sound).")]
		public readonly string ActiveCondition = "aot-fs-matrix-active";

		[GrantedConditionReference]
		[Desc("Condition auf dem Traeger-Aktor, solange die Power vollstaendig aufgeladen (bereit) ist.")]
		public readonly string ReadyCondition = "aot-fs-matrix-ready";

		[Desc("Effekt-Image fuer zerstoerte Flugobjekte (TS fsair).")]
		public readonly string KillEffectImage = "aot-fs-air";

		[SequenceReference(nameof(KillEffectImage))]
		public readonly string KillEffectSequence = "idle";

		[PaletteReference]
		public readonly string KillEffectPalette = "effect";

		[Desc("Sound beim Zerstoeren eines Flugobjekts / Abfangen einer Nuke.")]
		public readonly string KillSound = "firstrm1.aud";

		[Desc("Sound an der Traeger-Position beim Aktivieren.")]
		public readonly string ActivationSound = "firstrm1.aud";

		[Desc("EVA-Datei (UI, nur Besitzer) wenn die Matrix offline geht.")]
		public readonly string OfflineSpeech = "aot-eva-fs-offline.aud";

		[Desc("EVA-Datei (UI, nur Besitzer) wenn die Power vollstaendig geladen ist.")]
		public readonly string ReadySpeech = "aot-eva-fs-ready.aud";

		[Desc("DamageTypes fuer den Kill (Absturz-/Todesanimationen).")]
		public readonly BitSet<DamageType> DamageTypes = default;

		[Desc("Nur airborne Ziele (CenterPosition ueber Terrain) toeten.")]
		public readonly bool AirborneOnly = true;

		[Desc("Hoehe (ueber Terrain), unterhalb derer eine im Sinkflug befindliche Nuke ueber",
			"eingeschlossenem Gebiet abgefangen wird.")]
		public readonly WDist NukeInterceptAltitude = WDist.FromCells(7);

		[Desc("Zell-Intervall fuer das Nachfuehren der Wandliste waehrend der Aktivierung.")]
		public readonly int RefreshInterval = 10;

		public override object Create(ActorInitializer init) { return new AotFirestormMatrixPower(init.Self, this); }
	}

	public class AotFirestormMatrixPower : SupportPower, ITick, INotifyKilled, INotifySold, INotifyOwnerChanged
	{
		public readonly AotFirestormMatrixPowerInfo MatrixInfo;

		static readonly BitSet<CrushClass> HoverCrushClass = new("aothover");

		readonly HashSet<CPos> cells = [];
		readonly Dictionary<Actor, (ExternalCondition External, int Token)> wallTokens = [];

		bool active;
		int remaining;
		int refreshCountdown;
		int activeToken = Actor.InvalidConditionToken;
		int readyToken = Actor.InvalidConditionToken;
		bool readyAnnounced;

		public bool IsActive => active;

		public AotFirestormMatrixPower(Actor self, AotFirestormMatrixPowerInfo info)
			: base(self, info)
		{
			MatrixInfo = info;
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			// Sofort ausloesen, kein Ziel-Cursor.
			self.World.IssueOrder(new Order(order, self.Owner.PlayerActor, Target.Invalid, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			active = true;
			remaining = MatrixInfo.ActiveDuration;
			refreshCountdown = 0;
			readyAnnounced = false;

			if (activeToken == Actor.InvalidConditionToken && !string.IsNullOrEmpty(MatrixInfo.ActiveCondition))
				activeToken = self.GrantCondition(MatrixInfo.ActiveCondition);

			if (!string.IsNullOrEmpty(MatrixInfo.ActivationSound))
				Game.Sound.Play(SoundType.World, MatrixInfo.ActivationSound, self.CenterPosition);

			RefreshWalls(self);
		}

		void Deactivate(Actor self)
		{
			if (!active)
				return;

			active = false;
			cells.Clear();

			foreach (var kv in wallTokens)
				if (!kv.Key.IsDead)
					kv.Value.External.TryRevokeCondition(kv.Key, this, kv.Value.Token);

			wallTokens.Clear();

			if (activeToken != Actor.InvalidConditionToken)
				activeToken = self.RevokeCondition(activeToken);

			if (!string.IsNullOrEmpty(MatrixInfo.OfflineSpeech) && self.Owner == self.World.LocalPlayer)
				Game.Sound.Play(SoundType.UI, MatrixInfo.OfflineSpeech);
		}

		void RevokeReady(Actor self)
		{
			if (readyToken != Actor.InvalidConditionToken)
				readyToken = self.RevokeCondition(readyToken);
			readyAnnounced = false;
		}

		void RefreshWalls(Actor self)
		{
			cells.Clear();

			// Tote Segmente aus der Token-Liste raeumen (Token stirbt mit dem Aktor)
			foreach (var dead in wallTokens.Keys.Where(a => a.IsDead || !a.IsInWorld).ToList())
				wallTokens.Remove(dead);

			foreach (var a in self.World.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner != self.Owner)
					continue;

				if (!MatrixInfo.WallActors.Contains(a.Info.Name))
					continue;

				cells.Add(a.Location);

				if (!wallTokens.ContainsKey(a))
				{
					var external = a.TraitsImplementing<ExternalCondition>()
						.FirstOrDefault(t => t.Info.Condition == MatrixInfo.WallCondition && t.CanGrantCondition(this));

					if (external != null)
						wallTokens[a] = (external, external.GrantCondition(a, this));
				}
			}
		}

		void KillAt(Actor self, WPos pos)
		{
			var world = self.World;
			world.AddFrameEndTask(w => w.Add(new SpriteEffect(pos, w, MatrixInfo.KillEffectImage,
				MatrixInfo.KillEffectSequence, MatrixInfo.KillEffectPalette)));

			if (!string.IsNullOrEmpty(MatrixInfo.KillSound))
				Game.Sound.Play(SoundType.World, MatrixInfo.KillSound, pos);
		}

		void ITick.Tick(Actor self)
		{
			// ReadyCondition: solange die Ladung voll ist (Flame-Overlay auf SLAB), unabhaengig
			// von "active" -> deckt "Cooldown abgeschlossen" UND "Firestorm aktiv" zusammen mit
			// ActiveCondition ab (Aktor-YAML kombiniert beide per RequiresCondition: a || b).
			var manager = self.Owner.PlayerActor.TraitOrDefault<SupportPowerManager>();
			var ready = manager != null && manager.Powers.TryGetValue(Info.OrderName, out var instance) && instance.Ready;

			if (ready && readyToken == Actor.InvalidConditionToken && !string.IsNullOrEmpty(MatrixInfo.ReadyCondition))
				readyToken = self.GrantCondition(MatrixInfo.ReadyCondition);
			else if (!ready && readyToken != Actor.InvalidConditionToken)
				readyToken = self.RevokeCondition(readyToken);

			if (ready && !readyAnnounced)
			{
				readyAnnounced = true;
				if (!string.IsNullOrEmpty(MatrixInfo.ReadySpeech) && self.Owner == self.World.LocalPlayer)
					Game.Sound.Play(SoundType.UI, MatrixInfo.ReadySpeech);
			}
			else if (!ready)
				readyAnnounced = false;

			if (!active)
				return;

			if (IsTraitDisabled || IsTraitPaused || --remaining <= 0)
			{
				Deactivate(self);
				return;
			}

			if (--refreshCountdown <= 0)
			{
				refreshCountdown = MatrixInfo.RefreshInterval;
				RefreshWalls(self);
			}

			if (cells.Count == 0)
				return;

			// Luft-Kill-Zone: alle Flugeinheiten ueber Matrix-Zellen zerstoeren
			foreach (var pair in self.World.ActorsWithTrait<Aircraft>())
			{
				var a = pair.Actor;
				if (a.IsDead || !a.IsInWorld)
					continue;

				if (!cells.Contains(a.Location))
					continue;

				if (MatrixInfo.AirborneOnly && self.World.Map.DistanceAboveTerrain(a.CenterPosition) <= WDist.Zero)
					continue;

				KillAt(self, a.CenterPosition);
				a.Kill(self, MatrixInfo.DamageTypes);
			}

			// Hover-Infanterie (z.B. Zone Trooper, siehe AotHoverPassable.cs) ueberquert
			// Mauern/Tore genau wie Rock/River -- kein Aircraft-Trait, daher separat erfasst
			// ueber die "aothover"-Markierung im Locomotor.Crushes-Bitset.
			foreach (var pair in self.World.ActorsWithTrait<Mobile>())
			{
				var a = pair.Actor;
				if (a.IsDead || !a.IsInWorld)
					continue;

				if (!pair.Trait.Info.LocomotorInfo.Crushes.Overlaps(HoverCrushClass))
					continue;

				if (!cells.Contains(a.Location))
					continue;

				KillAt(self, a.CenterPosition);
				a.Kill(self, MatrixInfo.DamageTypes);
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { Deactivate(self); RevokeReady(self); }
		void INotifySold.Selling(Actor self) { }
		void INotifySold.Sold(Actor self) { Deactivate(self); RevokeReady(self); }
		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner) { Deactivate(self); RevokeReady(self); }

		// ==== Statische Schnittstelle fuer den NukeLaunch-Interception-Hook ====

		/// <summary>Liefert die aktive Matrix-Power, deren Zellen <paramref name="cell"/> enthalten (Feind von firedBy).</summary>
		public static AotFirestormMatrixPower FindInterceptorAt(World world, CPos cell, Player firedBy)
		{
			foreach (var pair in world.ActorsWithTrait<AotFirestormMatrixPower>())
			{
				var power = pair.Trait;
				if (!power.active || pair.Actor.IsDead)
					continue;

				if (firedBy != null && firedBy.RelationshipWith(pair.Actor.Owner) != PlayerRelationship.Enemy)
					continue;

				if (power.cells.Contains(cell))
					return power;
			}

			return null;
		}

		/// <summary>
		/// Einschluss-Check: liegt <paramref name="target"/> in einem Gebiet, das von den Matrix-Zellen
		/// einer aktiven feindlichen Matrix vollstaendig gegen den Kartenrand abgeriegelt ist (4er-BFS)?
		/// </summary>
		public static AotFirestormMatrixPower FindEnclosingInterceptor(World world, CPos target, Player firedBy)
		{
			foreach (var pair in world.ActorsWithTrait<AotFirestormMatrixPower>())
			{
				var power = pair.Trait;
				if (!power.active || pair.Actor.IsDead || power.cells.Count == 0)
					continue;

				if (firedBy != null && firedBy.RelationshipWith(pair.Actor.Owner) != PlayerRelationship.Enemy)
					continue;

				if (power.cells.Contains(target) || IsEnclosed(world, target, power.cells))
					return power;
			}

			return null;
		}

		static bool IsEnclosed(World world, CPos start, HashSet<CPos> barrier)
		{
			var map = world.Map;
			if (!map.Contains(start))
				return false;

			var visited = new HashSet<CPos> { start };
			var queue = new Queue<CPos>();
			queue.Enqueue(start);

			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				foreach (var dir in CVec.Directions)
				{
					// 4er-Nachbarschaft: Waende verbinden orthogonal, diagonale "Luecken" gibt es nicht
					if (dir.X != 0 && dir.Y != 0)
						continue;

					var n = c + dir;
					if (visited.Contains(n) || barrier.Contains(n))
						continue;

					// Kartenrand erreicht -> Gebiet ist offen
					if (!map.Contains(n))
						return false;

					visited.Add(n);
					queue.Enqueue(n);
				}
			}

			return true;
		}

		/// <summary>Abfang-Effekt + Sound an einer Position abspielen (vom NukeLaunch-Hook genutzt).</summary>
		public void PlayInterceptEffect(World world, WPos pos)
		{
			world.AddFrameEndTask(w => w.Add(new SpriteEffect(pos, w, MatrixInfo.KillEffectImage,
				MatrixInfo.KillEffectSequence, MatrixInfo.KillEffectPalette)));

			if (!string.IsNullOrEmpty(MatrixInfo.KillSound))
				Game.Sound.Play(SoundType.World, MatrixInfo.KillSound, pos);
		}

		public WDist InterceptAltitude => MatrixInfo.NukeInterceptAltitude;
	}
}
