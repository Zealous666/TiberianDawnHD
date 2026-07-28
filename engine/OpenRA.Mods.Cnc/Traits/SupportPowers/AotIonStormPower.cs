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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("aotmod: Ion Storm Superpower (Civilian Array). Verdunkelt fuer Duration Ticks schlagartig die",
		"gesamte Karte, laesst sporadisch Blitze einschlagen und haelt in dieser Zeit jedes Fluggeraet",
		"am Boden. Nachbau des TS-Ion-Storms (rules.ini: IonLightningFrequency/-Randomness/IonStormWarhead).",
		"Der Aktor (neutrale Struktur) muss zuerst von einem Ingenieur eingenommen werden.")]
	public class AotIonStormPowerInfo : SupportPowerInfo
	{
		[Desc("Gesamtdauer des Sturms in Ticks (Default 1500 = 60 Sekunden bei 25 tps).")]
		public readonly int Duration = 1500;

		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Waffe, die bei jedem Blitzeinschlag detoniert. Traegt Schaden + Einschlag-Optik.")]
		public readonly string Weapon = null;

		[Desc("Minimaler/maximaler Abstand zwischen zwei Blitzen in Ticks (zufaellig dazwischen).")]
		public readonly int MinStrikeDelay = 40;

		[Desc("Siehe " + nameof(MinStrikeDelay) + ".")]
		public readonly int MaxStrikeDelay = 120;

		[Desc("Prozent-Chance, dass ein Blitz in eine ZUFAELLIGE Zelle einschlaegt statt gezielt auf einen",
			"Aktor. TS-Original waere 90 (= 10% gezielte Treffer); das war hier deutlich zu toedlich,",
			"weil unsere Blitze Obelisk-Schaden machen und Zufallszellen zusaetzlich Treffer erzeugen.")]
		public readonly int Randomness = 97;

		[Desc("Gezielte Blitze schlagen nur bei Aktoren ein, deren Besitzer NICHT dieser Fraktion",
			"angehoert. Verhindert, dass der Sturm bevorzugt eigene Einheiten trifft.")]
		public readonly bool TargetOwnUnits = true;

		[Desc("Hoehe ueber dem Einschlagpunkt, aus der der Blitz gezeichnet wird (Wolkenhoehe).")]
		public readonly WDist Altitude = new(5120);

		[Desc("Globale Lichtintensitaet waehrend des Sturms (1.0 = Tag).")]
		public readonly float NightIntensity = 0.35f;

		[Desc("Globaler Farbstich waehrend des Sturms als R,G,B-Multiplikatoren.",
			"Leicht gelbstichig (User-Wunsch): R/G hoch, B gedaempft.")]
		public readonly float[] NightTint = [0.95f, 0.86f, 0.62f];

		[Desc("Sounds, die bei einem Blitzeinschlag GLEICHZEITIG abgespielt werden (Donner-Mischung).")]
		public readonly ImmutableArray<string> StrikeSounds = [];

		[Desc("Mindestabstand in Ticks zwischen zwei Donner-Wiedergaben (verhindert Sound-Spam).")]
		public readonly int SoundCooldown = 45;

		[Desc("Sounds, die beim Losbrechen des Sturms global abgespielt werden.")]
		public readonly ImmutableArray<string> ActivationSounds = [];

		[Desc("EVA-Ansage beim Losbrechen des Sturms (Dateiname). Wird nicht-positional fuer jeden",
			"Spieler abgespielt, nicht nur fuer den Ausloeser.")]
		public readonly string ActivationSpeech = null;

		[Desc("EVA-Ansage, wenn der Sturm abklingt.")]
		public readonly string EndSpeech = null;

		[Desc("Aktortypen, die waehrend des Sturms bewegungsunfaehig werden (Hovercraft).",
			"Sie werden NICHT zerstoert - dafuer muss der Aktor " + nameof(GroundedCondition) + " tragen.")]
		public readonly ImmutableArray<string> GroundedActorTypes = [];

		[Desc("ExternalCondition, die Helikopter (Aircraft mit VTOL) und die Typen aus",
			nameof(GroundedActorTypes) + " fuer die Sturmdauer erhalten. Am Aktor haengt daran ein",
			"PauseOnCondition auf Aircraft bzw. Mobile -> Zwangslandung statt Absturz.")]
		public readonly string GroundedCondition = "aot-ion-storm-grounded";

		[Desc("ExternalCondition, die ALLE Gebaeude aller Spieler fuer die Sturmdauer erhalten.",
			"Am ^Building haengt daran ein PowerMultiplier mit Modifier 0 -> Blackout.")]
		public readonly string BlackoutCondition = "aot-ion-storm-blackout";

		[Desc("Anzahl Karten-Zeilen, die pro Tick neu beleuchtet werden (gestreckte Invalidierung,",
			"verhindert einen Frame-Spike beim Umschalten). 0 = alles sofort.")]
		public readonly int SweepRows = 8;

		public override object Create(ActorInitializer init) { return new AotIonStormPower(init.Self, this); }
	}

	public class AotIonStormPower : SupportPower, ITick, INotifyOwnerChanged
	{
		// Wie oft die Welt nach neuen Fluggeraeten/Gebaeuden abgesucht wird (Ticks).
		const int RescanInterval = 25;

		readonly AotIonStormPowerInfo info;
		readonly WeaponInfo weapon;
		readonly TerrainLighting lighting;

		readonly Dictionary<Actor, List<(string Condition, ExternalCondition External, int Token)>> tokens = [];

		bool active;
		int remaining;
		int nextStrike;
		int soundCooldown;
		int nextRescan;

		// Beleuchtung, die vor dem Sturm aktiv war -> wird danach exakt wiederhergestellt.
		float savedIntensity;
		float3 savedTint;

		int sweepRow;
		int sweepRemaining;

		public AotIonStormPower(Actor self, AotIonStormPowerInfo info)
			: base(self, info)
		{
			this.info = info;
			weapon = self.World.Map.Rules.Weapons[info.Weapon.ToLowerInvariant()];
			lighting = self.World.WorldActor.TraitOrDefault<TerrainLighting>();
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			// Sofort ausloesen, kein Ziel-Cursor (wie AotIronDomePower).
			self.World.IssueOrder(new Order(order, self.Owner.PlayerActor, Target.Invalid, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			foreach (var s in info.ActivationSounds)
				if (!string.IsNullOrEmpty(s))
					Game.Sound.Play(SoundType.World, s, self.CenterPosition);

			// EVA-Warnung nicht-positional: Activate() laeuft auf jedem Client, also hoert sie
			// jeder Spieler - ein Ion Storm betrifft schliesslich die ganze Karte.
			if (!string.IsNullOrEmpty(info.ActivationSpeech))
				Game.Sound.Play(SoundType.UI, info.ActivationSpeech);

			// Schlagartig Nacht. Ein evtl. laufender Tag/Nacht-Zyklus wird fuer die Sturmdauer
			// komplett stillgelegt (Uhr haelt an, keine Lighting-Writes) - sonst wuerde er waehrend
			// einer Daemmerungs-Transition jeden Tick gegen unsere Sturm-Nacht anschreiben und beide
			// Seiten wuerden dauernd volle Karten-Sweeps ausloesen.
			SetCycleSuppressed(self, true);

			if (lighting != null)
			{
				savedIntensity = lighting.GlobalIntensity;
				savedTint = lighting.GlobalTint;

				var t = info.NightTint;
				lighting.SetGlobalLighting(info.NightIntensity,
					new float3(t.Length > 0 ? t[0] : 1f, t.Length > 1 ? t[1] : 1f, t.Length > 2 ? t[2] : 1f));
				StartSweep(self);
			}

			// Blackout, Zwangslandungen und Flugzeugabstuerze sofort anwenden.
			ApplyStormEffects(self);

			active = true;
			remaining = info.Duration;
			nextStrike = self.World.SharedRandom.Next(info.MinStrikeDelay, info.MaxStrikeDelay + 1);
			soundCooldown = 0;
			nextRescan = RescanInterval;
		}

		/// <summary>Legt einen laufenden Tag/Nacht-Zyklus still bzw. gibt ihn wieder frei.</summary>
		static void SetCycleSuppressed(Actor self, bool suppressed)
		{
			foreach (var cycle in self.World.WorldActor.TraitsImplementing<AotDayNightCycle>())
				cycle.Suppressed = suppressed;
		}

		void StartSweep(Actor self)
		{
			sweepRow = self.World.Map.Bounds.Top;
			sweepRemaining = self.World.Map.Bounds.Height;
		}

		void TickSweep(Actor self)
		{
			if (lighting == null || sweepRemaining <= 0)
				return;

			var rows = info.SweepRows <= 0 ? sweepRemaining : info.SweepRows;
			for (var i = 0; i < rows && sweepRemaining > 0; i++)
			{
				lighting.InvalidateRow(sweepRow++);
				sweepRemaining--;
			}
		}

		/// <summary>
		/// Verteilt die Sturm-Conditions und zerstoert Flugzeuge. Laeuft periodisch, damit auch
		/// waehrend des Sturms neu gebaute Einheiten/Gebaeude noch erfasst werden.
		/// Flugzeuge (Aircraft ohne VTOL) stuerzen ab; Helikopter (VTOL) und Hovercraft bekommen
		/// nur die Grounded-Condition und landen dadurch -> kein Verlust, nur Stillstand.
		/// </summary>
		void ApplyStormEffects(Actor self)
		{
			var world = self.World;
			var doomed = new List<Actor>();

			foreach (var pair in world.ActorsWithTrait<Aircraft>())
			{
				var a = pair.Actor;
				if (a.IsDead || !a.IsInWorld || a.Owner.NonCombatant)
					continue;

				if (pair.Trait.Info.VTOL)
				{
					// Helikopter: Zwangslandung ueber die Condition (Aircraft.PauseOnCondition).
					Grant(a, info.GroundedCondition);
					continue;
				}

				// Flugzeuge: nur abstuerzen lassen, wenn sie wirklich in der Luft sind.
				if (world.Map.DistanceAboveTerrain(a.CenterPosition).Length >= pair.Trait.Info.MinAirborneAltitude)
					doomed.Add(a);
			}

			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				// Blackout gilt fuer ALLE Spieler, auch fuer den Ausloeser selbst.
				if (!a.Owner.NonCombatant && a.Info.HasTraitInfo<PowerInfo>())
					Grant(a, info.BlackoutCondition);

				if (info.GroundedActorTypes.Contains(a.Info.Name))
					Grant(a, info.GroundedCondition);
			}

			foreach (var a in doomed)
				if (a.Info.HasTraitInfo<IHealthInfo>())
					a.Kill(self);
				else
					a.Dispose();
		}

		/// <summary>Gewaehrt eine ExternalCondition genau einmal pro Aktor und merkt sich das Token.</summary>
		void Grant(Actor a, string condition)
		{
			if (string.IsNullOrEmpty(condition))
				return;

			if (tokens.TryGetValue(a, out var granted) && granted.Any(g => g.Condition == condition))
				return;

			var external = a.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(t => t.Info.Condition == condition && t.CanGrantCondition(this));

			if (external == null)
				return;

			if (!tokens.TryGetValue(a, out granted))
				tokens[a] = granted = [];

			granted.Add((condition, external, external.GrantCondition(a, this)));
		}

		void RevokeAll()
		{
			foreach (var kv in tokens)
				if (!kv.Key.IsDead)
					foreach (var g in kv.Value)
						g.External.TryRevokeCondition(kv.Key, this, g.Token);

			tokens.Clear();
		}

		void Strike(Actor self)
		{
			var world = self.World;
			WPos impact;

			// TS-Verteilung: Randomness% zufaellige Zelle, Rest gezielt auf einen Aktor.
			// Der gezielte Anteil sorgt dafuer, dass verlaesslich ab und zu etwas getroffen wird,
			// statt das rein dem Zufall der Zellenwahl zu ueberlassen.
			var pickActor = world.SharedRandom.Next(100) >= info.Randomness;
			if (pickActor)
			{
				var candidates = world.Actors.Where(a =>
					!a.IsDead && a.IsInWorld && !a.Owner.NonCombatant &&
					(info.TargetOwnUnits || a.Owner != self.Owner) &&
					a.Info.HasTraitInfo<IHealthInfo>() && a.OccupiesSpace != null).ToList();

				impact = candidates.Count > 0
					? candidates[world.SharedRandom.Next(candidates.Count)].CenterPosition
					: RandomCell(self);
			}
			else
				impact = RandomCell(self);

			var source = impact + new WVec(WDist.Zero, WDist.Zero, info.Altitude);

			var args = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = WAngle.Zero,
				CurrentMuzzleFacing = () => WAngle.Zero,
				DamageModifiers = [],
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = source,
				CurrentSource = () => source,
				SourceActor = self,
				PassiveTarget = impact,
				GuidedTarget = Target.FromPos(impact),
			};

			if (args.Weapon.Projectile != null)
			{
				var projectile = args.Weapon.Projectile.Create(args);
				if (projectile != null)
					world.AddFrameEndTask(w => w.Add(projectile));
			}

			if (soundCooldown <= 0 && info.StrikeSounds.Length > 0)
			{
				// Beide Sounds GLEICHZEITIG -> Donner-Mischung (User-Wunsch).
				foreach (var s in info.StrikeSounds)
					if (!string.IsNullOrEmpty(s))
						Game.Sound.Play(SoundType.World, s, impact);

				soundCooldown = info.SoundCooldown;
			}
		}

		WPos RandomCell(Actor self)
		{
			var map = self.World.Map;
			var b = map.Bounds;
			var x = self.World.SharedRandom.Next(b.Left, b.Right);
			var y = self.World.SharedRandom.Next(b.Top, b.Bottom);
			return map.CenterOfCell(new MPos(x, y).ToCPos(map));
		}

		void Deactivate(Actor self)
		{
			if (!active)
				return;

			active = false;

			// Blackout aufheben, Helikopter/Hovercraft wieder freigeben.
			RevokeAll();

			// Erst den gemerkten Zustand zurueckschreiben, DANN den Zyklus freigeben: dessen
			// appliedIntensity/-Tint passen dann wieder exakt zur echten Beleuchtung, er laeuft
			// nahtlos an der angehaltenen Uhrzeit weiter.
			if (lighting != null)
			{
				lighting.SetGlobalLighting(savedIntensity, savedTint);
				StartSweep(self);
			}

			SetCycleSuppressed(self, false);

			if (!string.IsNullOrEmpty(info.EndSpeech))
				Game.Sound.Play(SoundType.UI, info.EndSpeech);
		}

		void ITick.Tick(Actor self)
		{
			TickSweep(self);

			if (!active)
				return;

			if (soundCooldown > 0)
				soundCooldown--;

			// Nicht jeden Tick die Welt scannen - einmal pro Sekunde reicht, um waehrend des
			// Sturms neu gebaute Flugzeuge/Gebaeude noch zu erfassen.
			if (--nextRescan <= 0)
			{
				ApplyStormEffects(self);
				nextRescan = RescanInterval;
			}

			if (--nextStrike <= 0)
			{
				Strike(self);
				nextStrike = self.World.SharedRandom.Next(info.MinStrikeDelay, info.MaxStrikeDelay + 1);
			}

			if (--remaining <= 0)
				Deactivate(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			Deactivate(self);
		}
	}
}
