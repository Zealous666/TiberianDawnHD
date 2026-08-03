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

namespace OpenRA.Mods.Common.Orders
{
	public abstract class GlobalButtonOrderGenerator<T> : OrderGenerator
	{
		readonly string order;

		protected override MouseActionType ActionType => MouseActionType.GlobalCommand;

		protected GlobalButtonOrderGenerator(World world, string order)
			: base(world)
		{
			this.order = order;
		}

		protected virtual bool IsValidTrait(T t)
		{
			return t.IsTraitEnabled();
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var underCursor = world.ScreenMap.ActorsAtMouse(mi)
				.Select(a => a.Actor)
				.FirstOrDefault(a => a.Owner == world.LocalPlayer && a.TraitsImplementing<T>()
					.Any(IsValidTrait));

			if (underCursor == null)
				yield break;

			yield return new Order(order, underCursor, false);
		}

		protected override void Tick(World world)
		{
			if (world.LocalPlayer != null &&
				world.LocalPlayer.WinState != WinState.Undefined)
				world.CancelInputMode();
		}

		protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world) { yield break; }

		protected abstract override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi);
	}

	public class PowerDownOrderGenerator : GlobalButtonOrderGenerator<ToggleConditionOnOrder>
	{
		public PowerDownOrderGenerator(World world)
			: base(world, "PowerDown") { }

		protected override bool IsValidTrait(ToggleConditionOnOrder t)
		{
			return !t.IsTraitDisabled && !t.IsTraitPaused;
		}

		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return OrderInner(world, cell, worldPixel, mi).Any() ? "powerdown" : "powerdown-blocked";
		}
	}

	public class SellOrderGenerator : GlobalButtonOrderGenerator<Sellable>
	{
		public SellOrderGenerator(World world)
			: base(world, "Sell") { }

		// aotmod (User-Fund 2026-08-01: "Verkauf-Cursor erscheint bei Einheiten auf dem FIX nicht,
		// stattdessen wird das FIX darunter verkauft"). Die Basisimplementierung nimmt schlicht den
		// ERSTEN Aktor unter der Maus mit aktivem Sellable -- ein Fahrzeug, das auf dem Repair Depot
		// parkt, ueberlappt aber zwangslaeufig mit dem Gebaeude, und ScreenMap.ActorsAtMouse liefert
		// keine fuer diesen Zweck brauchbare Reihenfolge. Nicht-Gebaeude gewinnen daher explizit --
		// dieselbe Erwartung wie bei der normalen Selektion, wo eine Einheit ueber dem Gebaeude
		// unter ihr angeklickt wird. Betrifft nur den Verkauf; ohne Fahrzeug darunter bleibt alles
		// exakt wie bisher.
		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var underCursor = world.ScreenMap.ActorsAtMouse(mi)
				.Select(a => a.Actor)
				.Where(a => a.Owner == world.LocalPlayer
					&& a.TraitsImplementing<Sellable>().Any(t => t.IsTraitEnabled()))
				.OrderBy(a => a.Info.HasTraitInfo<BuildingInfo>() ? 1 : 0)
				.FirstOrDefault();

			if (underCursor == null)
				yield break;

			yield return new Order("Sell", underCursor, false);
		}

		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var cursor = OrderInner(world, cell, worldPixel, mi)
				.SelectMany(o => o.Subject.TraitsImplementing<Sellable>())
				.Where(t => !t.IsTraitDisabled)
				.Select(si => si.Info.Cursor)
				.FirstOrDefault();

			return cursor ?? "sell-blocked";
		}
	}
}
