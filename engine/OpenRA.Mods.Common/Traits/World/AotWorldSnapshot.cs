#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 *
 * Snapshot save/load: writes the live world state to a file and rebuilds it on load, INSTEAD of
 * replaying the recorded order stream from frame 0.
 *
 * Why: an OpenRA save stores orders, not state, so loading re-simulates the entire match. Measured
 * on an Age 3 match: 43761 frames, 583 s (9.7 min) to load, and it grows linearly with playtime.
 * No amount of tick optimisation reaches a few seconds -- even an unrealistic 1 ms/frame would
 * still take 44 s. Rebuilding the state directly makes load time independent of match length.
 *
 * Accepted losses (agreed with the user, 2026-08-25): running activities are NOT serialised, so
 * units stand idle at their position after loading; projectiles in flight are dropped; bot modules
 * re-plan from scratch. Everything that defines the *situation* -- who owns what, where, how
 * damaged, how rich, what is explored -- is restored.
 *
 * Secondary benefit: without a replay there is no bot-suppression window, so the whole class of
 * save-load desyncs (see AotBaseBuilderBotModule.NudgeBlockers) cannot occur by construction.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Saves and restores the world state directly, so loading a save does not have to replay",
		"the whole match. Attach to the world actor.")]
	public class AotWorldSnapshotInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotWorldSnapshot(init.World); }
	}

	public class AotWorldSnapshot : IPostWorldLoaded, IPreventMapSpawn
	{
		public const int FormatVersion = 2;

		/// <summary>Appended to the .orasav path. A sibling file rather than a new section inside the
		/// save because the save format caps each of its yaml sections at 128 KB, far below what a
		/// late-game actor list needs.</summary>
		public const string FileExtension = ".snap";

		/// <summary>
		/// Stable actor keys, so state that REFERENCES actors (a bot mission's squad, a transit ticket)
		/// can survive the round trip. Actor objects and their ActorIDs are recreated on load, so a raw
		/// reference means nothing; the key is the actor's index in the snapshot's Actors section.
		///
		/// Only set while a snapshot is being written or restored, and only ever read from
		/// IGameSaveTraitData implementations, which run inside that window.
		/// </summary>
		public static IReadOnlyDictionary<Actor, int> SavingActorKeys { get; private set; }

		/// <summary>Restored actors by snapshot key; the counterpart to SavingActorKeys.</summary>
		public static IReadOnlyList<Actor> RestoredActors { get; private set; }

		/// <summary>Restored actor -> snapshot key, so KeyOf works during the load pass too (SavingActorKeys
		/// only exists in the process that wrote the snapshot). Without this the audit would format every
		/// actor reference as -1 on load and read as a mismatch against the saved key.</summary>
		static IReadOnlyDictionary<Actor, int> restoredKeys;

		/// <summary>Key for an actor while saving, or -1 if it is not part of the snapshot (a passenger,
		/// a bridge, an actor already dead). Callers must handle -1 rather than assume every actor has
		/// a key.</summary>
		public static int KeyOf(Actor actor)
		{
			if (actor == null)
				return -1;

			if (SavingActorKeys != null && SavingActorKeys.TryGetValue(actor, out var savedKey))
				return savedKey;

			if (restoredKeys != null && restoredKeys.TryGetValue(actor, out var loadedKey))
				return loadedKey;

			return -1;
		}

		/// <summary>Conditions the snapshot's audit recorded for each restored actor. A trait whose state
		/// lives in a granted condition can recover it from here when the snapshot predates that trait
		/// saving the state itself -- see SavedConditions.</summary>
		static Dictionary<Actor, HashSet<string>> restoredAuditConditions;

		/// <summary>The conditions this actor had when the snapshot was taken, or null if unknown. Query it
		/// from INotifySnapshotRestored to rebuild state that only existed as a granted condition. This is
		/// the general escape hatch for older snapshots: the audit records every condition, so state that a
		/// trait did not save explicitly is usually still recoverable instead of silently lost.</summary>
		public static IReadOnlyCollection<string> SavedConditions(Actor actor)
		{
			if (restoredAuditConditions != null && restoredAuditConditions.TryGetValue(actor, out var c))
				return c;

			return null;
		}

		/// <summary>The actor a key refers to while restoring, or null if the key is unknown -- which
		/// happens legitimately when the actor was not saved.</summary>
		public static Actor ActorFromKey(int key)
		{
			if (RestoredActors == null || key < 0 || key >= RestoredActors.Count)
				return null;

			return RestoredActors[key];
		}

		/// <summary>True while a snapshot restore is in progress, so map actors and starting units are
		/// suppressed -- the snapshot already contains every actor, including the map's own.</summary>
		public bool Restoring { get; private set; }

		/// <summary>True once this session's world came from a snapshot. Traits that hold knowledge the
		/// snapshot cannot carry (the bot base plan) use it to hold back destructive clean-up they would
		/// otherwise base on that missing knowledge.</summary>
		public static bool RestoredFromSnapshot { get; private set; }

		readonly World world;
		readonly string restorePath;

		public AotWorldSnapshot(World world)
		{
			this.world = world;

			// The path comes from the server's StartGame order (Game.PendingGameSaveSnapshot), which is
			// re-assigned on every game start -- so this can never pick up a leftover from an earlier,
			// cancelled load.
			if (world.Type == WorldType.Regular && !string.IsNullOrEmpty(Game.PendingGameSaveSnapshot))
			{
				restorePath = Game.PendingGameSaveSnapshot;
				Restoring = true;
			}
		}

		bool IPreventMapSpawn.PreventMapSpawn(World world, ActorReference actorReference)
		{
			return Restoring;
		}

		// IPostWorldLoaded, not IWorldLoaded: this runs after EVERY IWorldLoaded trait, so the resource
		// layer, shroud and the rest have finished initialising from the map and cannot overwrite what
		// is restored here. Map spawns are suppressed separately, through IPreventMapSpawn.
		void IPostWorldLoaded.PostWorldLoaded(World w, WorldRenderer wr)
		{
			if (!Restoring)
				return;

			try
			{
				Restore(w, restorePath);
				RestoredFromSnapshot = true;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[AotSnapshot] restore of '{restorePath}' failed: {e}");
				throw;
			}
			finally
			{
				// Map spawns are long past by now; leaving this set would keep suppressing anything
				// that spawns map actors later in the match.
				Restoring = false;

				// Consume the pending-snapshot signal NOW that it has done its job (it gated the loading
				// screen in World.LoadComplete and LoadWidgetAtGameStart). If it stayed set, the NEXT
				// world -- the shellmap when the player returns to the menu -- would hide its UI root and
				// never reveal it (a shellmap is not a Regular world, so no snapshot restore fires the
				// GameLoaded that un-hides it). That left the main menu invisible until a force-quit.
				Game.PendingGameSaveSnapshot = null;

				// World.LoadComplete opened the themed loading screen for us (there is no order replay to
				// drive it). Nothing will ever fire the matching GameLoaded -- that normally happens on the
				// replay's end edge -- so close it here, now that the world is fully restored. Same
				// notification the replay path uses: it hides the overlay and reveals the HUD.
				foreach (var nsr in w.WorldActor.TraitsImplementing<INotifyGameLoaded>())
					nsr.GameLoaded(w);

				foreach (var player in w.Players)
					foreach (var nsr in player.PlayerActor.TraitsImplementing<INotifyGameLoaded>())
						nsr.GameLoaded(w);
			}
		}

		#region Save

		/// <summary>Writes the current world state to `path`. Called on the client, which is the only
		/// side that has a world -- the server only ever sees orders.</summary>
		public static void Save(World world, string path)
		{
			var root = new List<MiniYamlNode>
			{
				new("Meta", new MiniYaml("", new List<MiniYamlNode>
				{
					new("Version", FormatVersion.ToStringInvariant()),
					new("WorldTick", world.WorldTick.ToStringInvariant())
				})),
				new("Players", new MiniYaml("", SavePlayers(world))),
				new("Actors", new MiniYaml("", SaveActors(world)))
			};

			var resources = SaveResources(world);
			if (resources != null)
				root.Add(new MiniYamlNode("Resources", new MiniYaml("", resources)));

			root.Add(new MiniYamlNode("Moves", new MiniYaml("", SaveMoves(world))));
			root.Add(new MiniYamlNode("Stores", new MiniYaml("", SaveStores(world))));
			root.Add(new MiniYamlNode("TraitData", new MiniYaml("", SaveTraitData(world))));
			root.Add(new MiniYamlNode("Production", new MiniYaml("", SaveProduction(world))));
			root.Add(new MiniYamlNode("Health", new MiniYaml("", SaveHealth(world))));
			root.Add(new MiniYamlNode("Bridges", new MiniYaml("", SaveBridges(world))));
			root.Add(new MiniYamlNode("Shroud", new MiniYaml("", SaveShroud(world))));

			// The complete authoritative snapshot of engine-defined state: every [VerifySync] member of
			// every ISync trait, which is exactly what the engine compares for multiplayer determinism.
			// Written so Verify() can diff it after load and report EVERY gap by name, instead of only
			// the categories someone remembered to hand-code. Must come last: it uses the actor keys
			// SaveActors published.
			root.Add(new MiniYamlNode("SyncAudit", new MiniYaml("", SaveSyncAudit(world))));
			root.Add(new MiniYamlNode("PlayerAudit", new MiniYaml("", SavePlayerAudit(world))));

			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, root.WriteToString());
		}

		static List<MiniYamlNode> SavePlayers(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var player in world.Players)
			{
				var fields = new List<MiniYamlNode>();

				var pr = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (pr != null)
				{
					fields.Add(new MiniYamlNode("Cash", pr.Cash.ToStringInvariant()));
					fields.Add(new MiniYamlNode("Resources", pr.Resources.ToStringInvariant()));
					fields.Add(new MiniYamlNode("Earned", pr.Earned.ToStringInvariant()));
					fields.Add(new MiniYamlNode("Spent", pr.Spent.ToStringInvariant()));
				}

				fields.Add(new MiniYamlNode("WinState", player.WinState.ToString()));

				if (fields.Count > 0)
					nodes.Add(new MiniYamlNode(player.InternalName, new MiniYaml("", fields)));
			}

			return nodes;
		}

		static List<MiniYamlNode> SaveActors(World world)
		{
			var nodes = new List<MiniYamlNode>();
			var keys = new Dictionary<Actor, int>();
			var index = 0;

			foreach (var actor in world.Actors)
			{
				if (!ShouldSave(world, actor))
					continue;

				var reference = BuildReference(actor);
				if (reference == null)
					continue;

				nodes.Add(new MiniYamlNode(index.ToStringInvariant(), reference.Save()));
				keys[actor] = index;
				index++;
			}

			// Published for the trait-data pass that follows, which is where actor references are saved.
			SavingActorKeys = keys;

			return nodes;
		}

		static bool ShouldSave(World world, Actor actor)
		{
			if (actor == world.WorldActor || actor.Disposed || !actor.IsInWorld)
				return false;

			// Skip the player actor itself (created with the player; handled by SavePlayers). But do NOT
			// skip every positionless actor -- upgrade/tech dummies (aot-turret-upgrade etc.) have no
			// OccupiesSpace yet PROVIDE prerequisites just by existing. Excluding them lost every bought
			// upgrade on load (verified: 113 'prereq.aot-*-upgrade expected 1 got absent' mismatches).
			if (actor.Owner?.PlayerActor == actor)
				return false;

			// Bridges are NOT map actors: LegacyBridgeLayer builds them from the map's tiles at
			// WorldLoaded, which IPreventMapSpawn does not cover. They therefore already exist when the
			// snapshot is restored, and saving them here would create a second one on top (verified:
			// "aot-arc-bridge2/Neutral: expected 1, got 2"). Their damage state is carried separately,
			// see SaveBridges.
			if (actor.Info.HasTraitInfo<BridgeInfo>())
				return false;

			// Actors that another actor deterministically re-creates on load (ore mine keep-out zone
			// and scattered ore/gem decoration) must not be saved -- the mine's INotifyAddedToWorld
			// re-spawns them on restore, and saving them here would leave a duplicate on top.
			if (actor.Info.HasTraitInfo<SnapshotTransientInfo>())
				return false;

			return true;
		}

		/// <summary>Rebuilds an ActorReference from a LIVE actor, the same way Transform does when it
		/// swaps one actor for another -- including letting traits contribute their own state through
		/// ITransformActorInitModifier (cargo, veterancy, ...).</summary>
		static ActorReference BuildReference(Actor actor)
		{
			// Built as a TypeDictionary first because that is what ITransformActorInitModifier expects
			// (and ActorReference does not expose its own dictionary outside the core assembly).
			var inits = new TypeDictionary
			{
				new OwnerInit(actor.Owner),

				// Buildings would otherwise replay their construction animation on load.
				new SkipMakeAnimsInit()
			};

			// Positionless actors (upgrade/tech dummies) get no location or facing -- they exist only to
			// provide a prerequisite. Everything below needs a position.
			if (actor.OccupiesSpace != null)
			{
				inits.Add(new LocationInit(actor.Location));

				// Units sharing a cell (infantry) must come back in the same sub-position, or several of
				// them would pile into one slot and the loser gets bumped somewhere else.
				foreach (var (cell, subCell) in actor.OccupiesSpace.OccupiedCells())
				{
					if (cell != actor.Location || subCell == SubCell.Invalid || subCell == SubCell.Any)
						continue;

					inits.Add(new SubCellInit(subCell));
					break;
				}

				// Aircraft in flight: LocationInit alone puts them back on the ground (the user saw a
				// flying helicopter come back landed). CenterPosition carries the altitude.
				var center = actor.CenterPosition;
				if (center != actor.World.Map.CenterOfCell(actor.Location))
					inits.Add(new CenterPositionInit(center));
			}

			var facing = actor.TraitOrDefault<IFacing>();
			if (facing != null)
				inits.Add(new FacingInit(facing.Facing));

			var health = actor.TraitOrDefault<IHealth>();
			if (health != null && health.MaxHP > 0)
				inits.Add(new HealthInit((int)((health.HP * 100L + health.MaxHP / 2) / health.MaxHP)));

			foreach (var modifier in actor.TraitsImplementing<ITransformActorInitModifier>())
				modifier.ModifyTransformActorInit(actor, inits);

			// Cargo contributes RuntimeCargoInit above, which holds LIVE actor objects and is marked
			// ISuppressInitExport -- ActorReference.Save drops it, so passengers would simply vanish
			// (they are not in the world themselves, so nothing else saves them either). Emit the
			// exportable CargoInit as well, which names the passenger types and has Cargo rebuild them.
			// Known limitation: a passenger's own health and veterancy are not carried across.
			foreach (var cargo in actor.TraitsImplementing<Cargo>())
			{
				var passengers = cargo.Passengers.Select(p => p.Info.Name).ToArray();
				if (passengers.Length > 0)
					inits.Add(new CargoInit(cargo.Info, passengers));
			}

			return new ActorReference(actor.Info.Name, inits);
		}

		/// <summary>
		/// Queued movement waypoints per unit, read from the same activity target-line chain the game
		/// draws (CurrentActivity -> NextActivity, TargetLineNodes). Full activity state is deliberately
		/// NOT serialised, but losing a unit's move order outright was too much (the user's units forgot
		/// where they were going). Only cell/terrain targets are captured -- a plain move or a waypoint
		/// path -- and re-issued as Move orders on load. Attack/follow orders against another actor are
		/// not carried yet; the unit falls back to idle-at-position for those.
		/// </summary>
		static List<MiniYamlNode> SaveMoves(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var actor in world.Actors)
			{
				var key = KeyOf(actor);
				if (key < 0 || actor.CurrentActivity == null)
					continue;

				if (actor.TraitOrDefault<IMove>() == null)
					continue;

				// Harvesters resume on their own (Harvester.Created -> FindAndDeliverResources). Re-issuing
				// a plain Move to the resource cell we read from their activity would REPLACE that harvest
				// with a one-off move -- the unit drives there and stops. So leave them out entirely.
				if (actor.Info.HasTraitInfo<HarvesterInfo>())
					continue;

				var cells = new List<string>();
				for (var a = actor.CurrentActivity; a != null; a = a.NextActivity)
				{
					if (a.IsCanceling)
						continue;

					foreach (var node in a.TargetLineNodes(actor))
					{
						// Only self-directed movement to ground. An Actor target is a follow/attack and
						// is left out on purpose (see summary).
						if (node.Target.Type == TargetType.Terrain)
						{
							var cell = world.Map.CellContaining(node.Target.CenterPosition);
							cells.Add($"{cell.X},{cell.Y}");
						}
					}
				}

				if (cells.Count > 0)
					nodes.Add(new MiniYamlNode(key.ToStringInvariant(), string.Join(" ", cells)));
			}

			return nodes;
		}

		/// <summary>
		/// Resource stores that are filled on creation and never re-filled from the map -- an ore mine's
		/// remaining trips, most importantly. Keyed by cell, since these actors do not move. Without
		/// this an ore mine comes back at 100 %, because OreMineDurability.Created tops it up to Capacity
		/// on every fresh actor (verified: mined-out mines were full again after loading).
		/// </summary>
		static List<MiniYamlNode> SaveStores(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var actor in world.Actors)
			{
				if (actor.Disposed || !actor.IsInWorld || actor.OccupiesSpace == null)
					continue;

				var store = actor.TraitsImplementing<IStoresResources>().FirstOrDefault();
				if (store == null)
					continue;

				var key = KeyOf(actor);
				if (key < 0)
					continue;

				var contents = store.Contents.Where(kv => kv.Value > 0).ToList();
				if (contents.Count == 0)
					continue;

				// Keyed by snapshot key, not cell: harvesters move, so a cell key is unreliable for them.
				nodes.Add(new MiniYamlNode(
					key.ToStringInvariant(),
					string.Join(" ", contents.Select(kv => $"{kv.Key},{kv.Value}"))));
			}

			return nodes;
		}

		/// <summary>
		/// The engine's own per-trait save channel (IGameSaveTraitData): control groups, selection,
		/// viewport, and every bot module's internal state.
		///
		/// The engine normally delivers this at the END of a save replay (World.Tick, on the
		/// wasLoadingGameSave edge). A snapshot load never enters that replay state, so without this
		/// the whole channel is silently dropped -- which is why control groups came back empty.
		///
		/// Keyed by STABLE IDENTITY -- the owning actor's snapshot key, the trait type, and the trait's
		/// occurrence on that actor -- NOT by global enumeration position. Position keying assumes the
		/// actor-creation order at restore reproduces the order at save exactly; any drift silently lands
		/// one trait's data on a different trait. For the per-building age latch that meant age conditions
		/// restoring onto the wrong buildings (the "manche age0, manche age1, manche age2 durcheinander"
		/// report -- and the grey age-2 yard/barracks, which are buildings whose age2 latch never arrived).
		/// Identity keys remove that dependency: each entry finds its exact trait regardless of order.
		/// </summary>
		static List<MiniYamlNode> SaveTraitData(World world)
		{
			var nodes = new List<MiniYamlNode>();
			var occ = new Dictionary<string, int>();

			foreach (var tp in world.ActorsWithTrait<IGameSaveTraitData>())
			{
				var data = tp.Trait.IssueTraitData(tp.Actor);
				if (data == null || data.Count == 0)
					continue;

				var key = TraitDataKey(world, tp.Actor, tp.Trait, occ);
				nodes.Add(new MiniYamlNode(key, new MiniYaml("", data)));
			}

			return nodes;
		}

		/// <summary>A stable identity for one IGameSaveTraitData trait: which actor it is on, its type, and
		/// which occurrence of that type on that actor. Independent of enumeration order.</summary>
		static string TraitDataKey(World world, Actor actor, IGameSaveTraitData trait, Dictionary<string, int> occ)
		{
			// Separators MUST avoid ':' -- MiniYaml splits a node on the first colon into key:value, so a
			// key like "p:Multi0" is written back as key "p", value "Multi0", and every entry collapses to
			// its prefix and stops matching (measured: 140/142 unmatched). '@' and '.' are both valid in
			// MiniYaml keys (e.g. WithIdleOverlay@tsremap) and are used here instead.
			string tag;
			if (actor == world.WorldActor)
				tag = "w";
			else if (actor.Owner?.PlayerActor == actor)
				tag = "p." + actor.Owner.InternalName;
			else
			{
				var key = KeyOf(actor);
				tag = key >= 0 ? "k." + key.ToStringInvariant() : "n." + actor.Info.Name;
			}

			var type = trait.GetType().Name;
			var baseKey = tag + "@" + type;
			occ.TryGetValue(baseKey, out var n);
			occ[baseKey] = n + 1;
			return baseKey + "@" + n.ToStringInvariant();
		}

		/// <summary>The ORIGINAL identity key scheme, which used ':' and '|'. MiniYaml splits a node key on
		/// the first ':', so these keys were written whole to the file but read back collapsed (key "p",
		/// value "Multi0|Type|0"). Saves written by that build (before the '@'/'.' fix) still hold the full
		/// key -- reconstructable by rejoining key+':'+value -- so recomputing this scheme over the live
		/// traits lets those saves load with full trait data instead of being thrown away. Keep forever:
		/// it is the only bridge to saves made in that window.</summary>
		static string TraitDataKeyOld(World world, Actor actor, IGameSaveTraitData trait, Dictionary<string, int> occ)
		{
			string tag;
			if (actor == world.WorldActor)
				tag = "w";
			else if (actor.Owner?.PlayerActor == actor)
				tag = "p:" + actor.Owner.InternalName;
			else
			{
				var key = KeyOf(actor);
				tag = key >= 0 ? "k:" + key.ToStringInvariant() : "n:" + actor.Info.Name;
			}

			var type = trait.GetType().Name;
			var baseKey = tag + "|" + type;
			occ.TryGetValue(baseKey, out var n);
			occ[baseKey] = n + 1;
			return baseKey + "|" + n.ToStringInvariant();
		}

		/// <summary>The identity a saved TraitData node points at, reconstructed independently of format.
		/// New '@' saves store the whole key in node.Key with an empty value. Old ':' saves were split by
		/// MiniYaml, so the tail landed in node.Value.Value -- rejoin it. (A snapshot value is only ever
		/// non-empty because of that split; SaveTraitData always writes an empty value.)</summary>
		static string SavedTraitDataKey(MiniYamlNode node)
		{
			if (string.IsNullOrEmpty(node.Value.Value))
				return node.Key;

			// The tail carries the node terminator that followed the (always empty) value, e.g. the line
			// "k:111|CashTrickler|0:" reads back as key "k" + value "111|CashTrickler|0:". Trim it, or the
			// rebuilt key keeps a trailing ':' and still misses (measured: 140 unmatched with the ':' left on).
			return (node.Key + ":" + node.Value.Value).TrimEnd(':');
		}

		/// <summary>
		/// Queued production, keyed by the producing actor's cell plus the queue type. Only what is in
		/// the queue is carried, not how far each item has come: the items are put back by re-issuing
		/// the normal StartProduction order on load, which is the only way to get a correctly wired
		/// queue entry (the engine builds each item's completion callback inside its own order
		/// handler). Cost already paid is refunded on restore so the restart is money-neutral.
		/// </summary>
		static List<MiniYamlNode> SaveProduction(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var actor in world.ActorsHavingTrait<ProductionQueue>())
			{
				if (actor.Disposed || !actor.IsInWorld)
					continue;

				foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				{
					var items = queue.AllQueued().ToList();
					if (items.Count == 0)
						continue;

					var paid = items.Sum(i => i.TotalCost - i.RemainingCost);
					var entries = new List<MiniYamlNode>
					{
						new("Items", string.Join(",", items.Select(i => i.Item))),
						new("Paid", paid.ToStringInvariant())
					};

					nodes.Add(new MiniYamlNode(
						$"{actor.Location.X},{actor.Location.Y},{queue.Info.Type}",
						new MiniYaml("", entries)));
				}
			}

			return nodes;
		}

		/// <summary>Bridges are rebuilt from the map by LegacyBridgeLayer, so only their damage state
		/// travels in the snapshot -- keyed by cell, since that is stable across the rebuild. This is
		/// what makes a repaired bridge stay repaired (and a demolished one stay demolished).</summary>
		/// <summary>
		/// Exact hit points per actor. The actor list carries health as a PERCENTAGE (HealthInit is defined
		/// that way), which quantises it: on a 24000 HP actor one percent is 240 HP, so almost every damaged
		/// unit came back slightly off (measured: 600+ audit lines, e.g. 23880 -> 24000). Cosmetic in
		/// isolation, but it buried the audit in noise, and noise is what lets a real regression pass
		/// unnoticed. Only actors that are actually damaged are listed, so a healthy army costs nothing.
		/// </summary>
		static List<MiniYamlNode> SaveHealth(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var actor in world.Actors)
			{
				var key = KeyOf(actor);
				if (key < 0)
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				if (health == null || health.MaxHP <= 0 || health.HP == health.MaxHP)
					continue;

				nodes.Add(new MiniYamlNode(key.ToStringInvariant(), health.HP.ToStringInvariant()));
			}

			return nodes;
		}

		static List<MiniYamlNode> SaveBridges(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var actor in world.ActorsHavingTrait<Bridge>())
			{
				if (actor.Disposed || !actor.IsInWorld)
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				if (health == null || health.MaxHP <= 0)
					continue;

				nodes.Add(new MiniYamlNode(
					$"{actor.Location.X},{actor.Location.Y}",
					health.HP.ToStringInvariant()));
			}

			return nodes;
		}

		static List<MiniYamlNode> SaveResources(World world)
		{
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (layer == null || layer.IsEmpty)
				return null;

			// One line per resource type, cells packed as "x,y,density" triples -- far more compact
			// than a node per cell, which matters for a map-sized layer.
			var byType = new Dictionary<string, List<string>>();
			foreach (var cell in world.Map.AllCells)
			{
				var contents = layer.GetResource(cell);
				if (contents.Type == null || contents.Density == 0)
					continue;

				if (!byType.TryGetValue(contents.Type, out var list))
					byType[contents.Type] = list = [];

				list.Add($"{cell.X},{cell.Y},{contents.Density}");
			}

			return byType
				.Select(kv => new MiniYamlNode(kv.Key, string.Join(" ", kv.Value)))
				.ToList();
		}

		#region Full sync audit -- the complete, self-maintaining verification

		// BindingFlags used by the engine's own sync-field discovery (Sync.GenerateHashFunc).
		const BindingFlags SyncBinding = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		// Per trait type: the [VerifySync] fields and properties, discovered once. This is the same set
		// the engine hashes, so anything it considers sync-relevant is audited automatically -- a new
		// trait with [VerifySync] members needs no work here.
		static readonly Dictionary<Type, (FieldInfo[] Fields, PropertyInfo[] Props)> SyncMembers = [];

		static (FieldInfo[] Fields, PropertyInfo[] Props) MembersOf(Type type)
		{
			lock (SyncMembers)
			{
				if (SyncMembers.TryGetValue(type, out var cached))
					return cached;

				var fields = type.GetFields(SyncBinding)
					.Where(f => f.IsDefined(typeof(VerifySyncAttribute), true)).ToArray();
				var props = type.GetProperties(SyncBinding)
					.Where(p => p.IsDefined(typeof(VerifySyncAttribute), true) && p.GetGetMethod(true) != null).ToArray();

				var result = (fields, props);
				SyncMembers[type] = result;
				return result;
			}
		}

		// Actor references are formatted as their snapshot key, so an actor changing ActorID on reload
		// (which it always does) does not read as a spurious change. Everything else is its invariant
		// string form.
		static string FormatSyncValue(object value)
		{
			switch (value)
			{
				case null: return "null";
				case Actor actor: return "actor#" + KeyOf(actor).ToString(CultureInfo.InvariantCulture);
				case Player player: return "player:" + player.InternalName;
				case Target target:
					return target.Type == TargetType.Actor
						? "target-actor#" + KeyOf(target.Actor).ToString(CultureInfo.InvariantCulture)
						: "target:" + target.Type + ":" + (target.Type == TargetType.Terrain ? target.CenterPosition.ToString() : "");
				case bool b: return b ? "True" : "False";
				default: return Convert.ToString(value, CultureInfo.InvariantCulture);
			}
		}

		// Per-player state that is not on any actor: support-power charge (a power can take 10+ minutes,
		// so its exact remaining time must round-trip) and owned prerequisites (age tiers, unlocks).
		// None of this is [VerifySync]; the verifier reads it to catch that class of gap.
		static List<MiniYamlNode> SavePlayerAudit(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var player in world.Players)
			{
				var line = AuditPlayer(player);
				if (line.Length > 0)
					nodes.Add(new MiniYamlNode(player.InternalName, line));
			}

			return nodes;
		}

		static string AuditPlayer(Player player)
		{
			var parts = new List<string>();

			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (spm != null)
				foreach (var kv in spm.Powers.OrderBy(k => k.Key))
					parts.Add($"power.{kv.Key}.remaining={kv.Value.RemainingTicks}");

			foreach (var pr in TechTree.OwnedPrerequisites(player).Where(p => p.Value > 0).OrderBy(p => p.Key))
				parts.Add($"prereq.{pr.Key}={pr.Value}");

			return string.Join(" ; ", parts);
		}

		// One line per actor: "trait.member=value" pairs for every synced member it has. Keyed by the
		// snapshot actor key so save and load line up.
		static List<MiniYamlNode> SaveSyncAudit(World world)
		{
			var nodes = new List<MiniYamlNode>();

			foreach (var kv in SavingActorKeys.OrderBy(k => k.Value))
			{
				var line = AuditActor(kv.Key);
				if (line.Length > 0)
					nodes.Add(new MiniYamlNode(kv.Value.ToStringInvariant(), line));
			}

			return nodes;
		}

		static string AuditActor(Actor actor)
		{
			var parts = new List<string>();
			foreach (var sync in actor.TraitsImplementing<ISync>())
			{
				var traitName = sync.GetType().Name;

				// Pure fog-of-war visibility -- recomputed from shroud, not gameplay state. It differs on
				// every load and is only noise in the audit.
				if (traitName == "FrozenUnderFog")
					continue;
				var (fields, props) = MembersOf(sync.GetType());

				foreach (var field in fields)
					parts.Add($"{traitName}.{field.Name}={FormatSyncValue(field.GetValue(sync))}");

				foreach (var prop in props)
					parts.Add($"{traitName}.{prop.Name}={FormatSyncValue(prop.GetValue(sync))}");
			}

			// Conditions: age, upgrades, deploy, cloak -- the biggest class of non-[VerifySync] state.
			// Sorted so the comparison is order-independent.
			foreach (var cond in actor.Conditions.Where(c => c.Value > 0).OrderBy(c => c.Key))
				parts.Add($"cond.{cond.Key}={cond.Value}");

			// Semicolon-separated: values never contain one, and it keeps the whole actor on a single
			// readable line in the .snap file.
			return string.Join(" ; ", parts);
		}

		#endregion

		static List<MiniYamlNode> SaveShroud(World world)
		{
			var nodes = new List<MiniYamlNode>();
			foreach (var player in world.Players)
			{
				var shroud = player.Shroud;
				if (shroud == null)
					continue;

				var explored = world.Map.ProjectedCells
					.Where(shroud.IsExplored)
					.Select(p => $"{p.U},{p.V}");

				nodes.Add(new MiniYamlNode(player.InternalName, string.Join(" ", explored)));
			}

			return nodes;
		}

		#endregion

		#region Restore

		static void Restore(World world, string path)
		{
			var root = new MiniYaml("", MiniYaml.FromString(File.ReadAllText(path), path)).ToDictionary();

			if (root.TryGetValue("Meta", out var meta))
			{
				var nodes = meta.ToDictionary();
				if (nodes.TryGetValue("Version", out var version)
					&& Exts.ParseInt32Invariant(version.Value) != FormatVersion)
					throw new InvalidDataException(
						$"This save was made with an older snapshot format (v{version.Value}); the game now writes v{FormatVersion}. " +
						"An old save does not contain the newly-saved state (ages, upgrades, ore-mine levels, ...), so it cannot be " +
						"restored correctly. Play and SAVE AGAIN with this build to get a compatible save.");
			}

			var playersByName = world.Players.ToDictionary(p => p.InternalName, p => p);

			// Actors first and NOT guarded: everything else references them, so if this fails the load
			// genuinely cannot continue. The remaining sections are each isolated below so one failure
			// costs only that piece of state, never the whole game.
			if (root.TryGetValue("Actors", out var actors))
			{
				// Conditions recorded per actor in the audit section. Used as a FALLBACK source for state
				// that an older snapshot did not store as an init -- see DeployedFromAudit.
				root.TryGetValue("SyncAudit", out var auditForActors);
				RestoreActors(world, actors, playersByName, auditForActors);
			}

			Section("Players", root, r => RestorePlayers(r, playersByName));

			if (root.TryGetValue("Resources", out var resources))
				RestoreResources(world, resources);

			if (root.TryGetValue("Stores", out var stores))
				RestoreStores(world, stores);

			if (root.TryGetValue("TraitData", out var traitData))
				RestoreTraitData(world, traitData);

			if (root.TryGetValue("Production", out var production))
				RestoreProduction(world, production);

			if (root.TryGetValue("Health", out var health))
				RestoreHealth(world, health);

			if (root.TryGetValue("Bridges", out var bridges))
				RestoreBridges(world, bridges);

			if (root.TryGetValue("Shroud", out var shroud))
				RestoreShroud(world, shroud, playersByName);

			if (root.TryGetValue("Moves", out var moves))
				RestoreMoves(world, moves);

			// Force every actor to re-cache its render palettes. Condition-gated sprite bodies (the
			// Gemini age-2 buildings: a grey "effect" base body plus a player-colour REMAP overlay gated
			// on aot-age2-active) are only enabled once RestoreTraitData grants the latched age condition
			// -- AFTER the actors were created. Their remap overlays could otherwise keep a stale/empty
			// palette reference and render grey (the user saw age-2 yard and barracks lose their colour).
			foreach (var actor in world.Actors)
				foreach (var rs in actor.TraitsImplementing<Render.RenderSprites>())
					rs.UpdatePalette();

			// Arm the render-time probe so the next handful of frames log the actual palette the age-2
			// yard/barracks remap overlays resolve to (the grey-building diagnosis).
			Render.RenderSprites.SnapshotProbeFrames = 4;

			// Let any trait fix up what lived only in a lost activity queue. Done last, so IsIdle is
			// accurate (moves already re-issued above). A delivery aircraft that was flying off to despawn
			// uses this to resume leaving instead of hovering forever (AotLeaveOnLoad).
			foreach (var actor in world.Actors.ToList())
			{
				if (actor.Disposed || !actor.IsInWorld)
					continue;

				foreach (var n in actor.TraitsImplementing<INotifySnapshotRestored>())
					n.SnapshotRestored(actor);
			}

			Log.Write("debug", $"[AotSnapshot] restored '{path}'");

			Verify(world, root);
		}

		// Runs one restore section, isolating its failures: a gap in one piece of state must never take
		// the whole load down. Actors are deliberately NOT run through this (see Restore).
		static void Section(string key, Dictionary<string, MiniYaml> root, Action<MiniYaml> restore)
		{
			if (!root.TryGetValue(key, out var data))
				return;

			try
			{
				restore(data);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[AotSnapshot] section '{key}' failed to restore (skipped): {e}");
			}
		}

		static void RestoreActors(World world, MiniYaml actors, Dictionary<string, Player> playersByName,
			MiniYaml audit = null)
		{
			// Conditions the audit recorded for each actor key, used to recover deploy state from snapshots
			// written before GrantConditionOnDeploy carried it as an init.
			var auditConditions = ParseAuditConditions(audit);

			// Index-aligned with the Actors section, so a saved key resolves to the same actor.
			var byKey = new List<Actor>();
			var created = 0;
			var index = -1;
			foreach (var node in actors.Nodes)
			{
				index++;
				var reference = new ActorReference(node.Value.Value, node.Value);

				// Recover a missing deploy state. Snapshots taken before GrantConditionOnDeploy implemented
				// ITransformActorInitModifier have no DeployStateInit, so every deployed unit came back
				// undeployed -- mobile gap/stealth generators lost their field, the NOD outpost its stealth,
				// the cruiser its carrier mode. The audit section of those same files DOES record the
				// granted condition, so the state is recoverable rather than lost.
				if (reference.GetOrDefault<DeployStateInit>() == null
					&& auditConditions.TryGetValue(index, out var conditions)
					&& DeployedFromAudit(world, node.Value.Value, conditions))
					reference.Add(new DeployStateInit(DeployState.Deployed));

				// Re-add the skip flag HERE, on the way in. BuildReference sets it when saving, but
				// ActorReference.Save() drops every ISuppressInitExport init (ActorReference.cs) and
				// SkipMakeAnimsInit is one -- so it never reaches the file (verified: 0 occurrences of
				// "SkipMakeAnims" in a real .snap). Without it every restored building replays its
				// construction animation, which re-grants build-incomplete: buildings visibly rebuild
				// themselves on load, and the condition hides the player-colour remap overlay, leaving
				// the Gemini age-2 buildings grey.
				reference.Add(new SkipMakeAnimsInit());

				// An owner that no longer exists would throw deep inside actor creation; fall back to
				// the world owner exactly as SpawnMapActors does.
				var owner = reference.GetOrDefault<OwnerInit>();
				if (owner == null || !playersByName.ContainsKey(owner.InternalName))
					reference.Replace(new OwnerInit(world.WorldActor.Owner));

				byKey.Add(world.CreateActor(true, reference));
				created++;
			}

			RestoredActors = byKey;

			var keys = new Dictionary<Actor, int>();
			var conditionsByActor = new Dictionary<Actor, HashSet<string>>();
			for (var i = 0; i < byKey.Count; i++)
			{
				keys[byKey[i]] = i;
				if (auditConditions.TryGetValue(i, out var c))
					conditionsByActor[byKey[i]] = c;
			}

			restoredKeys = keys;
			restoredAuditConditions = conditionsByActor;

			Log.Write("debug", $"[AotSnapshot] created {created} actor(s)");
		}

		static void RestorePlayers(MiniYaml players, Dictionary<string, Player> playersByName)
		{
			foreach (var node in players.Nodes)
			{
				if (!playersByName.TryGetValue(node.Key, out var player))
					continue;

				var fields = node.Value.ToDictionary();
				var pr = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (pr != null)
				{
					if (fields.TryGetValue("Cash", out var cash))
						pr.Cash = Exts.ParseInt32Invariant(cash.Value);

					if (fields.TryGetValue("Resources", out var resources))
						pr.Resources = Exts.ParseInt32Invariant(resources.Value);

					if (fields.TryGetValue("Earned", out var earned))
						pr.Earned = Exts.ParseInt32Invariant(earned.Value);

					if (fields.TryGetValue("Spent", out var spent))
						pr.Spent = Exts.ParseInt32Invariant(spent.Value);
				}
			}
		}

		static void RestoreResources(World world, MiniYaml resources)
		{
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (layer == null)
				return;

			// Wipe first: the layer is already populated from the map, so cells that were harvested
			// away during the match would otherwise survive the load (verified: "Tiberium: expected
			// 258 cells, got 264" -- exactly the cells that had been mined out).
			foreach (var cell in world.Map.AllCells)
				layer.ClearResources(cell);

			foreach (var node in resources.Nodes)
			{
				var type = node.Key;
				foreach (var triple in node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = triple.Split(',');
					if (parts.Length != 3)
						continue;

					var cell = new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1]));
					var density = (byte)Exts.ParseInt32Invariant(parts[2]);

					layer.ClearResources(cell);
					if (layer.CanAddResource(type, cell, density))
						layer.AddResource(type, cell, density);
				}
			}
		}

		static void RestoreStores(World world, MiniYaml stores)
		{
			foreach (var node in stores.Nodes)
			{
				var actor = ActorFromKey(Exts.ParseInt32Invariant(node.Key));
				var store = actor?.TraitsImplementing<IStoresResources>().FirstOrDefault();
				if (store == null)
					continue;

				foreach (var pair in node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = pair.Split(',');
					if (parts.Length != 2)
						continue;

					var type = parts[0];
					var target = Exts.ParseInt32Invariant(parts[1]);
					var have = store.Contents.TryGetValue(type, out var c) ? c : 0;

					// Created filled it to Capacity; take the surplus back off to reach the saved level.
					if (have > target)
						store.RemoveResource(type, have - target);
					else if (have < target)
						store.AddResource(type, target - have);
				}
			}
		}

		static void RestoreTraitData(World world, MiniYaml traitData)
		{
			// Old snapshots used pure-integer positional keys ("0","1",...). Detect that and fall back to
			// the legacy positional restore so an existing save still loads (best effort -- positional is
			// exactly the fragile scheme we moved away from, but it is what those bytes were written with).
			var legacy = traitData.Nodes.Any() && traitData.Nodes.All(n => int.TryParse(n.Key, out _));
			if (legacy)
			{
				RestoreTraitDataLegacy(world, traitData);
				return;
			}

			// Identity-keyed restore: map the LIVE traits by identity, then match each saved entry to its
			// exact trait -- order-independent. Register BOTH the current ('@') and the original (':')
			// key schemes so saves from either build load. The saved key is reconstructed format-agnostically
			// by SavedTraitDataKey (rejoining the ':' split that MiniYaml applied to old keys).
			var occNew = new Dictionary<string, int>();
			var occOld = new Dictionary<string, int>();
			var byKey = new Dictionary<string, TraitPair<IGameSaveTraitData>>();
			foreach (var tp in world.ActorsWithTrait<IGameSaveTraitData>())
			{
				byKey[TraitDataKey(world, tp.Actor, tp.Trait, occNew)] = tp;
				byKey[TraitDataKeyOld(world, tp.Actor, tp.Trait, occOld)] = tp;
			}

			var restored = 0;
			var unmatched = 0;
			foreach (var node in traitData.Nodes)
			{
				var savedKey = SavedTraitDataKey(node);
				if (!byKey.TryGetValue(savedKey, out var tp))
				{
					// A saved trait with no live counterpart: the actor or trait is gone. Under positional
					// keying this would have silently corrupted a NEIGHBOUR; now it is visible and skipped.
					unmatched++;
					Log.Write("debug", $"[AotSnapshot] trait data has no live trait for key '{savedKey}' -- skipped");
					continue;
				}

				try
				{
					tp.Trait.ResolveTraitData(tp.Actor, node.Value);
					restored++;
				}
				catch (Exception e)
				{
					// One bad trait must not abort the whole load -- it would leave the player with no
					// game at all. Log which trait failed and carry on; the rest still restores.
					Log.Write("debug", $"[AotSnapshot] trait data restore failed for {tp.Trait.GetType().Name} " +
						$"on {tp.Actor.Info.Name}: {e.Message}");
				}
			}

			Log.Write("debug", $"[AotSnapshot] restored trait data for {restored} trait(s), {unmatched} unmatched");
		}

		static void RestoreTraitDataLegacy(World world, MiniYaml traitData)
		{
			var traits = world.ActorsWithTrait<IGameSaveTraitData>().ToList();
			var restored = 0;

			foreach (var node in traitData.Nodes)
			{
				var index = Exts.ParseInt32Invariant(node.Key);
				if (index < 0 || index >= traits.Count)
					continue;

				var tp = traits[index];
				try
				{
					tp.Trait.ResolveTraitData(tp.Actor, node.Value);
					restored++;
				}
				catch (Exception e)
				{
					Log.Write("debug", $"[AotSnapshot] trait data restore failed for {tp.Trait.GetType().Name} " +
						$"on {tp.Actor.Info.Name}: {e.Message}");
				}
			}

			Log.Write("debug", $"[AotSnapshot] restored trait data for {restored} trait(s) (legacy positional)");
		}

		static void RestoreProduction(World world, MiniYaml production)
		{
			foreach (var node in production.Nodes)
			{
				var key = node.Key.Split(',');
				if (key.Length != 3)
					continue;

				var cell = new CPos(Exts.ParseInt32Invariant(key[0]), Exts.ParseInt32Invariant(key[1]));
				var queueType = key[2];

				var producer = world.ActorMap.GetActorsAt(cell)
					.FirstOrDefault(a => a.TraitsImplementing<ProductionQueue>().Any(q => q.Info.Type == queueType));

				if (producer == null)
					continue;

				var fields = node.Value.ToDictionary();
				if (!fields.TryGetValue("Items", out var items))
					continue;

				// Hand back what the player had already paid into these items, because re-queueing
				// charges for them again from the start.
				if (fields.TryGetValue("Paid", out var paid))
				{
					var pr = producer.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
					pr?.GiveCash(Exts.ParseInt32Invariant(paid.Value));
				}

				foreach (var item in items.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
					world.IssueOrder(Order.StartProduction(producer, item, 1));
			}
		}

		/// <summary>The condition names the audit recorded for each actor key. Empty when the snapshot has
		/// no audit section.</summary>
		static Dictionary<int, HashSet<string>> ParseAuditConditions(MiniYaml audit)
		{
			var result = new Dictionary<int, HashSet<string>>();
			if (audit == null)
				return result;

			foreach (var node in audit.Nodes)
			{
				if (!int.TryParse(node.Key, out var key))
					continue;

				var conditions = new HashSet<string>();
				foreach (var member in ParseAudit(node.Value.Value))
					if (member.Key.StartsWith("cond.", StringComparison.Ordinal) && member.Value != "0")
						conditions.Add(member.Key["cond.".Length..]);

				if (conditions.Count > 0)
					result[key] = conditions;
			}

			return result;
		}

		/// <summary>True when this actor type deploys via GrantConditionOnDeploy and the audit shows its
		/// deployed condition was granted. Reading the rules rather than a hard-coded list keeps every
		/// deployable unit covered, including ones added later.</summary>
		static bool DeployedFromAudit(World world, string actorType, HashSet<string> conditions)
		{
			if (string.IsNullOrEmpty(actorType) || !world.Map.Rules.Actors.TryGetValue(actorType, out var info))
				return false;

			foreach (var deploy in info.TraitInfos<GrantConditionOnDeployInfo>())
				if (!string.IsNullOrEmpty(deploy.DeployedCondition) && conditions.Contains(deploy.DeployedCondition))
					return true;

			return false;
		}

		/// <summary>How much cash RestoreProduction handed back to each player, so the audit can expect it
		/// instead of reporting it as a cash mismatch. Mirrors RestoreProduction's own lookup exactly.</summary>
		static Dictionary<string, int> RefundsByPlayer(World world, Dictionary<string, MiniYaml> root)
		{
			var refunds = new Dictionary<string, int>();
			if (!root.TryGetValue("Production", out var production))
				return refunds;

			foreach (var node in production.Nodes)
			{
				var key = node.Key.Split(',');
				if (key.Length != 3)
					continue;

				var fields = node.Value.ToDictionary();
				if (!fields.TryGetValue("Items", out _) || !fields.TryGetValue("Paid", out var paid))
					continue;

				var cell = new CPos(Exts.ParseInt32Invariant(key[0]), Exts.ParseInt32Invariant(key[1]));
				var queueType = key[2];
				var producer = world.ActorMap.GetActorsAt(cell)
					.FirstOrDefault(a => a.TraitsImplementing<ProductionQueue>().Any(q => q.Info.Type == queueType));

				if (producer?.Owner == null)
					continue;

				var name = producer.Owner.InternalName;
				refunds.TryGetValue(name, out var have);
				refunds[name] = have + Exts.ParseInt32Invariant(paid.Value);
			}

			return refunds;
		}

		/// <summary>Corrects each actor's health to the exact saved value, undoing the percentage
		/// quantisation of HealthInit (see SaveHealth).</summary>
		static void RestoreHealth(World world, MiniYaml health)
		{
			foreach (var node in health.Nodes)
			{
				var actor = ActorFromKey(Exts.ParseInt32Invariant(node.Key));
				if (actor == null || actor.IsDead || !actor.IsInWorld)
					continue;

				var h = actor.TraitOrDefault<Health>();
				if (h == null || h.MaxHP <= 0)
					continue;

				var target = Exts.ParseInt32Invariant(node.Value.Value).Clamp(1, h.MaxHP);
				var delta = h.HP - target;
				if (delta != 0)
					h.InflictDamage(actor, world.WorldActor, new Damage(delta), true);
			}
		}

		static void RestoreBridges(World world, MiniYaml bridges)
		{
			var byCell = new Dictionary<CPos, Actor>();
			foreach (var actor in world.ActorsHavingTrait<Bridge>())
				if (!actor.Disposed && actor.IsInWorld)
					byCell[actor.Location] = actor;

			foreach (var node in bridges.Nodes)
			{
				var parts = node.Key.Split(',');
				if (parts.Length != 2)
					continue;

				var cell = new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1]));
				if (!byCell.TryGetValue(cell, out var actor))
					continue;

				var health = actor.TraitOrDefault<Health>();
				if (health == null)
					continue;

				var target = Exts.ParseInt32Invariant(node.Value.Value);

				// A DEAD bridge cannot be healed by InflictDamage (it early-returns on IsDead), which is
				// why a repaired bridge came back destroyed ("expected 26400, got 0"). Resurrect it first,
				// then trim to the exact saved HP.
				if (health.IsDead && target > 0)
					health.Resurrect(actor, world.WorldActor);

				// Conversely, drive a live bridge down to a saved destroyed/damaged state.
				var delta = health.HP - target;
				if (delta > 0)
					health.InflictDamage(actor, world.WorldActor, new Damage(delta), true);
				else if (delta < 0 && !health.IsDead)
					health.InflictDamage(actor, world.WorldActor, new Damage(delta), true);
			}
		}

		static void RestoreMoves(World world, MiniYaml moves)
		{
			foreach (var node in moves.Nodes)
			{
				var actor = ActorFromKey(Exts.ParseInt32Invariant(node.Key));
				if (actor == null || actor.IsDead || !actor.IsInWorld)
					continue;

				var queued = false;
				foreach (var pair in node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = pair.Split(',');
					if (parts.Length != 2)
						continue;

					var cell = new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1]));

					// Re-issue as ordinary Move orders (queued after the first), so the unit picks its
					// path deterministically from its restored position -- no activity state smuggled in.
					world.IssueOrder(new Order("Move", actor, Target.FromCell(world, cell), queued));
					queued = true;
				}
			}
		}

		static void RestoreShroud(World world, MiniYaml shroud, Dictionary<string, Player> playersByName)
		{
			foreach (var node in shroud.Nodes)
			{
				if (!playersByName.TryGetValue(node.Key, out var player) || player.Shroud == null)
					continue;

				var cells = new List<PPos>();
				foreach (var pair in node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					var parts = pair.Split(',');
					if (parts.Length != 2)
						continue;

					cells.Add(new PPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1])));
				}

				player.Shroud.ExploreProjectedCells(cells);
			}
		}

		#endregion


		#region Verify

		/// <summary>
		/// Compares the world that was just rebuilt against the snapshot it came from and logs every
		/// difference. A snapshot load has no sync check to fall back on -- there is no replay to
		/// compare against -- so this is the equivalent safety net: it catches state that the snapshot
		/// does not carry yet (a production queue, a custom terrain layer, a granted condition) as a
		/// concrete mismatch in debug.log instead of leaving it to be noticed mid-match.
		/// </summary>
		static void Verify(World world, Dictionary<string, MiniYaml> root)
		{
			// Dedicated timestamped log so the verify result survives -- OpenRA OVERWRITES debug.log on
			// every launch, so returning to the menu after a load wipes the diagnosis. This file does not.
			// AddChannel is idempotent (returns early if the channel already exists), so this is safe to
			// call on every restore; the first call in the process fixes the filename.
			const string channel = "aotsnapshot";
			Log.AddChannel(channel, $"aotsnapshot-{DateTime.UtcNow:yyyy-MM-ddTHHmmssZ}.log");

			var problems = new List<string>();

			// Actors, grouped by type and owner so the comparison does not depend on creation order.
			if (root.TryGetValue("Actors", out var savedActors))
			{
				var expected = new Dictionary<string, int>();
				foreach (var node in savedActors.Nodes)
				{
					var owner = node.Value.Nodes.FirstOrDefault(n => n.Key == "Owner")?.Value.Value ?? "?";
					var key = $"{node.Value.Value}/{owner}";
					expected[key] = expected.GetValueOrDefault(key) + 1;
				}

				var actual = new Dictionary<string, int>();
				foreach (var actor in world.Actors)
				{
					if (!ShouldSave(world, actor))
						continue;

					var key = $"{actor.Info.Name}/{actor.Owner.InternalName}";
					actual[key] = actual.GetValueOrDefault(key) + 1;
				}

				foreach (var kv in expected)
				{
					var have = actual.GetValueOrDefault(kv.Key);
					if (have != kv.Value)
						problems.Add($"actor {kv.Key}: expected {kv.Value}, got {have}");
				}

				foreach (var kv in actual)
					if (!expected.ContainsKey(kv.Key))
						problems.Add($"actor {kv.Key}: unexpected {kv.Value}");
			}

			// Player economy. Restoring production hands back what was already paid into each queued item
			// (re-queueing charges for it again), so the expected cash is the saved cash PLUS that refund.
			// Accounting for it here keeps the audit trustworthy: an unexplained cash difference is then a
			// real bug, instead of being lost among refunds that are working as designed.
			var refunds = RefundsByPlayer(world, root);
			if (root.TryGetValue("Players", out var savedPlayers))
			{
				foreach (var node in savedPlayers.Nodes)
				{
					var player = world.Players.FirstOrDefault(p => p.InternalName == node.Key);
					var pr = player?.PlayerActor.TraitOrDefault<PlayerResources>();
					if (pr == null)
						continue;

					var fields = node.Value.ToDictionary();
					refunds.TryGetValue(node.Key, out var refund);
					if (fields.TryGetValue("Cash", out var cash)
						&& Exts.ParseInt32Invariant(cash.Value) + refund != pr.Cash)
						problems.Add($"{node.Key} cash: expected {cash.Value}" +
							(refund != 0 ? $" (+{refund} production refund)" : "") + $", got {pr.Cash}");

					if (fields.TryGetValue("Resources", out var res)
						&& Exts.ParseInt32Invariant(res.Value) != pr.Resources)
						problems.Add($"{node.Key} ore: expected {res.Value}, got {pr.Resources}");
				}
			}

			// Resource layer, compared as a total per type rather than cell by cell.
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (layer != null && root.TryGetValue("Resources", out var savedResources))
			{
				foreach (var node in savedResources.Nodes)
				{
					var expectedCells = node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
					var actualCells = world.Map.AllCells.Count(c =>
					{
						var contents = layer.GetResource(c);
						return contents.Type == node.Key && contents.Density > 0;
					});

					if (expectedCells != actualCells)
						problems.Add($"resource {node.Key}: expected {expectedCells} cells, got {actualCells}");
				}
			}

			// Bridges: HP per cell (they carry damage state, not an actor count).
			if (root.TryGetValue("Bridges", out var savedBridges))
			{
				var actual = new Dictionary<CPos, int>();
				foreach (var actor in world.ActorsHavingTrait<Bridge>())
				{
					var health = actor.TraitOrDefault<IHealth>();
					if (health != null && !actor.Disposed && actor.IsInWorld)
						actual[actor.Location] = health.HP;
				}

				foreach (var node in savedBridges.Nodes)
				{
					var parts = node.Key.Split(',');
					if (parts.Length != 2)
						continue;

					var cell = new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1]));
					var expected = Exts.ParseInt32Invariant(node.Value.Value);
					var have = actual.TryGetValue(cell, out var hp) ? hp : -1;
					if (have != expected)
						problems.Add($"bridge {cell}: expected HP {expected}, got {have}");
				}
			}

			// Resource stores (ore-mine trips etc.), total per cell.
			if (root.TryGetValue("Stores", out var savedStores))
			{
				foreach (var node in savedStores.Nodes)
				{
					var expectedTotal = node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
						.Sum(t => Exts.ParseInt32Invariant(t.Split(',')[1]));

					var actor = ActorFromKey(Exts.ParseInt32Invariant(node.Key));
					var store = actor?.TraitsImplementing<IStoresResources>().FirstOrDefault();
					var actualTotal = store?.Contents.Values.Sum() ?? -1;

					if (actualTotal != expectedTotal)
						problems.Add($"store #{node.Key}: expected {expectedTotal}, got {actualTotal}");
				}
			}

			// THE authoritative check: every [VerifySync] member of every restored actor, compared to
			// the snapshot. This is complete by construction -- whatever the engine treats as state is
			// audited, so a gap surfaces here by name instead of being noticed mid-match. The hand-rolled
			// checks above stay only for non-actor state (resource layer cells, shroud) the audit misses.
			if (root.TryGetValue("SyncAudit", out var savedAudit) && RestoredActors != null)
			{
				var auditProblems = 0;
				foreach (var node in savedAudit.Nodes)
				{
					var key = Exts.ParseInt32Invariant(node.Key);
					var actor = ActorFromKey(key);
					if (actor == null || actor.IsDead || !actor.IsInWorld)
					{
						problems.Add($"sync actor#{key}: missing after restore");
						continue;
					}

					var expected = ParseAudit(node.Value.Value);
					var actual = ParseAudit(AuditActor(actor));

					foreach (var member in expected.Keys)
					{
						var want = expected[member];
						if (!actual.TryGetValue(member, out var have) || have != want)
						{
							// Cap the noise: report the actor's type + first few differing members, then
							// stop -- the pattern is what matters, not 500 identical lines.
							if (auditProblems < 60)
								problems.Add($"sync actor#{key} ({actor.Info.Name}) {member}: expected '{want}', got '{(have ?? "<absent>")}'");

							auditProblems++;
						}
					}

					// Bidirectional: also flag state that is PRESENT after restore but was ABSENT at save.
					// This catches a spuriously-granted condition (e.g. build-incomplete re-granted by a
					// replayed make animation), which hides a player-colour remap overlay and greys the
					// building -- invisible to the one-directional check above.
					foreach (var member in actual.Keys)
					{
						if (!expected.ContainsKey(member))
						{
							if (auditProblems < 60)
								problems.Add($"sync actor#{key} ({actor.Info.Name}) {member}: unexpected '{actual[member]}' (absent at save)");

							auditProblems++;
						}
					}
				}

				if (auditProblems > 60)
					problems.Add($"sync: ...and {auditProblems - 60} more field mismatch(es) (raise the cap to see them)");
			}

			// Round-trip audit of the engine's OWN declared save state: every IGameSaveTraitData trait
			// (bot operations, support-power charge, ice/vein/foundation layers, latched age conditions,
			// control groups, selection, viewport, every vanilla bot module). Re-issue each trait's data
			// on the restored world and compare it to what was saved -- any trait, present or future,
			// that does not round-trip is reported here by name, with no per-trait code in the verifier.
			if (root.TryGetValue("TraitData", out var savedTraitData))
			{
				var legacy = savedTraitData.Nodes.Any() && savedTraitData.Nodes.All(n => int.TryParse(n.Key, out _));
				var occNew = new Dictionary<string, int>();
				var occOld = new Dictionary<string, int>();
				var byKey = new Dictionary<string, TraitPair<IGameSaveTraitData>>();
				var traits = world.ActorsWithTrait<IGameSaveTraitData>().ToList();
				if (!legacy)
					foreach (var tp in traits)
					{
						byKey[TraitDataKey(world, tp.Actor, tp.Trait, occNew)] = tp;
						byKey[TraitDataKeyOld(world, tp.Actor, tp.Trait, occOld)] = tp;
					}

				foreach (var node in savedTraitData.Nodes)
				{
					TraitPair<IGameSaveTraitData> tp;
					if (legacy)
					{
						var index = Exts.ParseInt32Invariant(node.Key);
						if (index < 0 || index >= traits.Count)
						{
							problems.Add($"traitdata #{index}: no trait at that index after restore");
							continue;
						}

						tp = traits[index];
					}
					else if (!byKey.TryGetValue(SavedTraitDataKey(node), out tp))
					{
						problems.Add($"traitdata '{SavedTraitDataKey(node)}': no matching trait after restore");
						continue;
					}

					var current = tp.Trait.IssueTraitData(tp.Actor);
					var currentText = current != null ? current.WriteToString().Trim() : "";
					var savedText = node.Value.Nodes.WriteToString().Trim();
					if (currentText != savedText)
						problems.Add($"traitdata {tp.Trait.GetType().Name} on {tp.Actor.Info.Name}: did not round-trip");
				}
			}

			// Per-player audit: support-power charge and prerequisites.
			if (root.TryGetValue("PlayerAudit", out var savedPlayerAudit))
			{
				foreach (var node in savedPlayerAudit.Nodes)
				{
					var player = world.Players.FirstOrDefault(p => p.InternalName == node.Key);
					if (player == null)
						continue;

					var expected = ParseAudit(node.Value.Value);
					var actual = ParseAudit(AuditPlayer(player));
					foreach (var member in expected.Keys)
						if (!actual.TryGetValue(member, out var have) || have != expected[member])
							problems.Add($"player {node.Key} {member}: expected '{expected[member]}', got '{(have ?? "<absent>")}'");
				}
			}

			if (problems.Count == 0)
			{
				Log.Write("debug", "[AotSnapshot] verify: restored world matches the snapshot");
				Log.Write(channel, "verify: restored world matches the snapshot");
				return;
			}

			Log.Write("debug", $"[AotSnapshot] verify: {problems.Count} MISMATCH(ES)");
			Log.Write(channel, $"verify: {problems.Count} MISMATCH(ES)");
			foreach (var problem in problems)
			{
				Log.Write("debug", $"[AotSnapshot]   {problem}");
				Log.Write(channel, $"  {problem}");
			}
		}

		static Dictionary<string, string> ParseAudit(string line)
		{
			var result = new Dictionary<string, string>();
			foreach (var pair in line.Split(" ; ", StringSplitOptions.RemoveEmptyEntries))
			{
				var eq = pair.IndexOf('=');
				if (eq > 0)
					result[pair[..eq]] = pair[(eq + 1)..];
			}

			return result;
		}

		#endregion
	}
}
