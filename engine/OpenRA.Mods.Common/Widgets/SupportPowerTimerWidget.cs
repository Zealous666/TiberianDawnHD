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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class SupportPowerTimerWidget : Widget
	{
		[FluentReference("player", "support-power", "time")]
		const string Format = "support-power-timer";

		// aotmod (User-Wunsch 2026-08-01): Variante ohne Spielernamen -- "Ion Cannon: 04:12" statt
		// "Cabal's Ion Cannon: 04:12". Die Zuordnung passiert allein ueber die Textfarbe, die unten
		// ohnehin schon auf die Spielerfarbe des Besitzers gesetzt wird.
		// Bewusst ein ZWEITER Key statt eines gekuerzten `support-power-timer`: CheckFluentReferences
		// verlangt, dass ein String GENAU die im FluentReference-Attribut deklarierten Variablen
		// referenziert (fehlende -> Error). Ein Key kann also nicht mal mit und mal ohne $player
		// existieren, und RA benutzt den bestehenden Key unveraendert weiter.
		[FluentReference("support-power", "time")]
		const string FormatWithoutPlayer = "support-power-timer-no-player";

		[Desc("Omit the owning player's name and rely on the player-coloured text alone.")]
		public readonly bool ShowPlayerName = true;

		public readonly string Font = "Bold";
		public readonly TextAlign Align = TextAlign.Left;
		public readonly TimerOrder Order = TimerOrder.Descending;

		readonly SpriteFont font;
		readonly IEnumerable<SupportPowerInstance> powers;
		readonly Color bgDark, bgLight;
		(string Text, Color Color)[] texts;

		[ObjectCreator.UseCtor]
		public SupportPowerTimerWidget(World world)
		{
			powers = world.ActorsWithTrait<SupportPowerManager>()
				.Where(p => !p.Actor.IsDead && !p.Actor.Owner.NonCombatant)
				.SelectMany(s => s.Trait.Powers.Values)
				.Where(p => p.Instances.Count > 0 && p.Info.DisplayTimerRelationships != PlayerRelationship.None && !p.Disabled);

			bgDark = ChromeMetrics.Get<Color>("TextContrastColorDark");
			bgLight = ChromeMetrics.Get<Color>("TextContrastColorLight");
			font = Game.Renderer.Fonts[Font];
		}

		public override void Tick()
		{
			var displayedPowers = powers.Where(p =>
			{
				// A power that counts down only AFTER it is bought has nothing to show before that:
				// RemainingTicks is zero, and the row read "1st Tiberium Age: 0:00" for every player
				// from the first second of the match (User 2026-08-05). It belongs here exactly while
				// the research runs, which is also the only time it tells other players anything.
				if (!p.CountsDownBeforePurchase && !p.Counting)
					return false;

				var owner = p.Instances[0].Self.Owner;
				var viewer = owner.World.RenderPlayer ?? owner.World.LocalPlayer;
				return viewer == null || p.Info.DisplayTimerRelationships.HasRelationship(owner.RelationshipWith(viewer));
			});

			texts = displayedPowers.Select(p =>
			{
				var self = p.Instances[0].Self;
				var time = WidgetUtils.FormatTime(p.RemainingTicks, false, self.World.Timestep);
				var text = ShowPlayerName
					? FluentProvider.GetMessage(Format,
						"player", self.Owner.ResolvedPlayerName,
						"support-power", p.Name,
						"time", time)
					: FluentProvider.GetMessage(FormatWithoutPlayer,
						"support-power", p.Name,
						"time", time);

				var color = !p.Ready || Game.LocalTick % 50 < 25 ? self.OwnerColor() : Color.White;

				return (text, color);
			}).ToArray();
		}

		public override void Draw()
		{
			if (!IsVisible() || texts == null)
				return;

			var y = 0;
			foreach (var t in texts)
			{
				var textSize = font.Measure(t.Text);
				var location = new float2(Bounds.X, Bounds.Y + y);

				if (Align == TextAlign.Center)
					location += new int2((Bounds.Width - textSize.X) / 2, 0);

				if (Align == TextAlign.Right)
					location += new int2(Bounds.Width - textSize.X, 0);

				font.DrawTextWithShadow(t.Text, location, t.Color, bgDark, bgLight, 1);
				y += (font.Measure(t.Text).Y + 5) * (int)Order;
			}
		}

		public enum TimerOrder { Ascending = -1, Descending = 1 }
	}
}
