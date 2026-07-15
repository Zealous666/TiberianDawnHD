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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Iron Dome Superpower. Macht alle eigenen Einheiten und Gebaeude fuer ActiveDuration Ticks",
		"unzerstoerbar (ExternalCondition aot-iron-dome auf allen eigenen Aktoren). Keine Zielauswahl.",
		"Der Aktor (neutrale Struktur) muss von einem Ingenieur eingenommen werden.")]
	public class AotIronDomePowerInfo : SupportPowerInfo
	{
		[Desc("Dauer der Unzerstoerbarkeit in Ticks (Standard: 1500 = 1 Minute bei 25 tps).")]
		public readonly int ActiveDuration = 1500;

		[Desc("ExternalCondition, die auf alle eigenen Aktoren verteilt wird (muss auf ^AotIronDomeable vorhanden sein).")]
		public readonly string InvulnerabilityCondition = "aot-iron-dome";

		[Desc("Sound beim Aktivieren (UI, nur lokaler Spieler).")]
		public readonly string ActivationSound = "";

		public override object Create(ActorInitializer init) { return new AotIronDomePower(init.Self, this); }
	}

	public class AotIronDomePower : SupportPower, ITick, INotifyOwnerChanged
	{
		readonly AotIronDomePowerInfo info;
		readonly Dictionary<Actor, (ExternalCondition External, int Token)> tokens = [];

		bool active;
		int remaining;

		public AotIronDomePower(Actor self, AotIronDomePowerInfo info)
			: base(self, info)
		{
			this.info = info;
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

			if (!string.IsNullOrEmpty(info.ActivationSound) && self.Owner == self.World.LocalPlayer)
				Game.Sound.Play(SoundType.UI, info.ActivationSound);

			// Bedingung auf alle lebenden eigenen Aktoren verteilen.
			foreach (var a in self.World.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner != self.Owner)
					continue;

				var external = a.TraitsImplementing<ExternalCondition>()
					.FirstOrDefault(t => t.Info.Condition == info.InvulnerabilityCondition && t.CanGrantCondition(this));

				if (external != null)
					tokens[a] = (external, external.GrantCondition(a, this));
			}

			active = true;
			remaining = info.ActiveDuration;
		}

		void Deactivate(Actor self)
		{
			if (!active)
				return;

			active = false;

			foreach (var kv in tokens)
				if (!kv.Key.IsDead)
					kv.Value.External.TryRevokeCondition(kv.Key, this, kv.Value.Token);

			tokens.Clear();
		}

		void ITick.Tick(Actor self)
		{
			if (!active)
				return;

			// Tote Aktoren aus der Liste raeumen.
			foreach (var dead in tokens.Keys.Where(a => a.IsDead || !a.IsInWorld).ToList())
				tokens.Remove(dead);

			if (--remaining <= 0)
				Deactivate(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			Deactivate(self);
		}
	}
}
