#region Copyright & License Information
/*
 * Age of Tiberium mod — spawns a free-standing actor (e.g. a lingering hazard cloud)
 * at the point of impact, owned by the attacking player.
 */
#endregion

using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Warheads
{
	[Desc("Spawns an actor at the point of impact.")]
	public class AotSpawnActorWarhead : Warhead, IRulesetLoaded<WeaponInfo>
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor type to spawn at the impact position.")]
		public readonly string Actor = null;

		[Desc("Skip spawning if an actor of the same type already exists within this range of the impact. Zero disables the check.")]
		public readonly WDist DedupeRange = WDist.Zero;

		[Desc("Only spawn the actor if the impact cell is a normal, placeable cell: passable terrain (per",
			"PlaceabilityLocomotor) and not occupied by a building or wall. Impacts on rock, cliffs or",
			"structures instead detonate FallbackWeapon (mine cluster missiles).")]
		public readonly bool RequirePlaceable = false;

		[Desc("Locomotor whose terrain passability decides whether the impact cell counts as normal ground.",
			"Only used when RequirePlaceable is true.")]
		public readonly string PlaceabilityLocomotor = "foot";

		[WeaponReference]
		[Desc("Weapon detonated at the impact point instead of spawning the actor, when RequirePlaceable is",
			"true and the cell is not placeable. Empty = nothing happens (the actor is simply skipped).")]
		public readonly string FallbackWeapon = null;

		WeaponInfo fallbackWeapon;

		public void RulesetLoaded(Ruleset rules, WeaponInfo info)
		{
			if (string.IsNullOrEmpty(FallbackWeapon))
				return;

			if (!rules.Weapons.TryGetValue(FallbackWeapon.ToLowerInvariant(), out fallbackWeapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{FallbackWeapon.ToLowerInvariant()}'");
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid)
				return;

			var firedBy = args.SourceActor;
			var world = firedBy.World;
			var cell = world.Map.CellContaining(target.CenterPosition);

			if (!world.Map.Contains(cell))
				return;

			// Mine cluster missiles: minen, die auf Felsen/Klippen oder auf einem Gebaeude/Mauer landen
			// wuerden, erscheinen nicht dort -- sie detonieren stattdessen sofort bei Kontakt.
			if (RequirePlaceable && !CellIsPlaceable(world, cell))
			{
				fallbackWeapon?.Impact(Target.FromPos(target.CenterPosition), firedBy);
				return;
			}

			if (DedupeRange > WDist.Zero)
				foreach (var a in world.FindActorsInCircle(target.CenterPosition, DedupeRange))
					if (!a.IsDead && a.IsInWorld && a.Info.Name == Actor)
						return;

			world.AddFrameEndTask(w => w.CreateActor(Actor, new TypeDictionary
			{
				new LocationInit(cell),
				new OwnerInit(firedBy.Owner),
			}));
		}

		bool CellIsPlaceable(World world, CPos cell)
		{
			// Von einem Gebaeude oder einer Mauer belegt (auch feindlich)? -> keine normale Zelle.
			var bi = world.WorldActor.TraitOrDefault<BuildingInfluence>();
			if (bi != null && bi.AnyBuildingAt(cell))
				return false;

			// Unbegehbares Gelaende (Fels, Klippe, ungueltiges Terrain)? -> keine normale Zelle.
			// Transiente Einheiten zaehlen NICHT als Blocker: die Zelle bleibt normal, die Mine
			// wird dort platziert (Terrain-Kosten, nicht Actor-Belegung).
			var loco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == PlaceabilityLocomotor);
			if (loco != null && loco.MovementCostForCell(cell) == PathGraph.MovementCostForUnreachableCell)
				return false;

			return true;
		}
	}
}
