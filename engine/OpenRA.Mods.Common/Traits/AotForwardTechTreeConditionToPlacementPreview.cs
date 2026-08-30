#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Makes the BUILDING PLACEMENT GHOST for a condition-gated single-actor sprite switch (e.g. the
 * Age-2 Gemini re-skins on aot-hpad-nod) show the sprite the real building will actually spawn
 * with, instead of always falling back to the EnabledByDefault body.
 *
 * AotForwardConditionsToPreview (see that file) solves the analogous problem for previews built
 * from a LIVE actor (a unit picked up by a Carryall) by forwarding self.Conditions. A placement
 * ghost has no live actor yet -- nothing has evaluated GrantConditionOnPrerequisite/
 * AotGrantConditionOnPrerequisiteLatched, so there are no real conditions to forward.
 *
 * What we can do instead: ask the placing player's TechTree whether they already own the
 * prerequisite that the condition is gated on. For a plain GrantConditionOnPrerequisite(Latched)
 * setup that is exactly the condition state the freshly created actor will start with (the grant
 * fires immediately on creation if the prerequisite is already satisfied), so this predicts the
 * real outcome correctly for the common "one actor, Age-gated re-skin" case.
 *
 * IActorPreviewInitInfo can't do this: ActorPreviewInits(ActorInfo, ActorPreviewType) is a static
 * rules-level call with no Owner/Player in scope, so it cannot query a specific player's TechTree.
 * Wired instead directly into PlaceBuildingOrderGenerator.VariantWrapper, which already has the
 * placing queue's Owner on hand.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: makes the building placement ghost show the Age-gated sprite the real building",
		"will start with, by checking the placing player's TechTree for Prerequisites and forwarding",
		"Condition accordingly via AotPreviewConditionsInit. See AotForwardTechTreeConditionToPlacementPreview.cs.")]
	public class AotForwardTechTreeConditionToPlacementPreviewInfo : TraitInfo
	{
		[Desc("Condition to report as granted/not-granted in the placement preview.")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		[Desc("Prerequisites (same syntax as GrantConditionOnPrerequisite) that decide whether",
			"Condition is reported as granted in the preview.")]
		public readonly string[] Prerequisites = [];

		public override object Create(ActorInitializer init) { return new AotForwardTechTreeConditionToPlacementPreview(); }
	}

	public class AotForwardTechTreeConditionToPlacementPreview { }
}
