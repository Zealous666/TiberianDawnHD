#region Copyright & License Information
/*
 * Age of Tiberium mod addition.
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
	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: keeps a unit type topped up to the count of a REFERENCE building type (e.g. Ore",
		"Transporters matched to Construction Yards) -- one per reference building, replaced ONLY when one is",
		"actually lost, never proactively over-built. Unlike HarvesterBotModule (which this mirrors) this does",
		"NOT require the unit to have a Harvester trait, so it also works for non-harvester economy units like",
		"the Ore Transporter. Requests go through the same IBotRequestUnitProduction channel every other bot",
		"module uses (e.g. HarvesterBotModule, McvExpansionManagerBotModule) -- completely independent of, and",
		"never redundant with, any UnitBuilderBotModule UnitLimits/UnitsToBuild entry for the same actor.")]
	public class AotReplacementUnitBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Actor types considered as the replaceable unit (e.g. the Ore Transporter). First buildable type is",
			"requested; leave with a single entry unless variants share age-gated Prerequisites like other roles.")]
		public readonly HashSet<string> UnitTypes = [];

		[Desc("Actor types whose current alive count sets the target (e.g. Construction Yards) -- one unit per",
			"alive reference building, no more.")]
		public readonly HashSet<string> ReferenceBuildingTypes = [];

		[Desc("Target floor regardless of reference building count (0 = purely reference-building-driven).")]
		public readonly int MinimumCount = 0;

		[Desc("Hard ceiling on the target, regardless of how many reference buildings exist",
			"(0 = no ceiling). Needed since base expansion (User 2026-08-03): a second construction",
			"yard would otherwise raise the Ore Transporter target to 2, but the expansion is meant to",
			"get only the transporter its yard spawns for free -- 'die expansion soll keinen eigenen",
			"oreT bekommen'.")]
		public readonly int MaximumCount = 0;

		[Desc("Ticks between checks.")]
		public readonly int ScanInterval = 250;

		public override object Create(ActorInitializer init) { return new AotReplacementUnitBotModule(init.Self, this); }
	}

	public class AotReplacementUnitBotModule : ConditionalTrait<AotReplacementUnitBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		IBotRequestUnitProduction[] requestUnitProduction;
		int ticks;

		public AotReplacementUnitBotModule(Actor self, AotReplacementUnitBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			requestUnitProduction = self.Owner.PlayerActor.TraitsImplementing<IBotRequestUnitProduction>().ToArray();
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || Info.UnitTypes.Count == 0 || Info.ReferenceBuildingTypes.Count == 0)
				return;

			if (--ticks > 0)
				return;

			ticks = Info.ScanInterval;

			var unitBuilder = requestUnitProduction.FirstEnabledTraitOrDefault();
			if (unitBuilder == null)
				return;

			var unitCount = world.Actors.Count(a => a.Owner == player && !a.IsDead && Info.UnitTypes.Contains(a.Info.Name));
			var refCount = world.Actors.Count(a => a.Owner == player && !a.IsDead && Info.ReferenceBuildingTypes.Contains(a.Info.Name));
			var target = Info.MinimumCount > refCount ? Info.MinimumCount : refCount;
			if (Info.MaximumCount > 0 && target > Info.MaximumCount)
				target = Info.MaximumCount;

			if (unitCount >= target)
				return;

			// Below target and about to try: from here on every exit is a REASON, and a silent one was
			// exactly the problem. A bot that had lost both ore transporters showed ECONOMY-EMERGENCY
			// in its status and still never queued a replacement, with nothing in the log to say why
			// (User 2026-08-10). Logged sparsely -- this runs on a scan interval, and a genuinely
			// unbuildable transporter would otherwise fill the file.
			var buildable = false;

			// First currently-buildable type (matches the age-variant pattern used elsewhere in this mod --
			// RoleVariants/BuildableVariant -- without needing access to that machinery here).
			var queue = AIUtils.FindQueuesByCategory(player).SelectMany(g => g).ToList();

			// IBotRequestUnitProduction.RequestedProductionCount only reflects the transient pending-
			// request list (UnitBuilderBotModule.queuedBuildRequests) -- that list empties out again within
			// a handful of ticks once the request is dequeued into the REAL production queue, long before
			// the unit actually finishes building. With a 250-tick ScanInterval longer than a single ORET's
			// build time, checking only RequestedProductionCount let this module re-request another one on
			// every scan while the previous request was still under construction, stacking several in the
			// queue that then all completed close together (confirmed via user report: several Ore
			// Transporters appearing at once shortly after a new Construction Yard). Checking the actual
			// production queue state (AllQueued) is what genuinely reflects "one is already being built".
			foreach (var type in Info.UnitTypes)
			{
				if (!world.Map.Rules.Actors.TryGetValue(type, out var ai))
					continue;

				if (!queue.Any(q => q.CanBuild(ai)))
					continue;

				buildable = true;

				if (queue.Any(q => q.AllQueued().Any(i => i.Item == type)))
					return;

				if (unitBuilder.RequestedProductionCount(bot, type) == 0)
				{
					unitBuilder.RequestUnitProduction(bot, type);
					Log.Write("debug", $"[AotReplace][{player.PlayerName}] {type} requested ({unitCount}/{target} alive)");
				}

				return;
			}

			if (++noBuildLog % 8 == 1)
				Log.Write("debug", $"[AotReplace][{player.PlayerName}] cannot replace {string.Join("/", Info.UnitTypes)} " +
					$"({unitCount}/{target} alive): " +
					(buildable ? "already queued" : "no production queue can build it -- prerequisite building lost?"));
		}

		int noBuildLog;
	}
}
