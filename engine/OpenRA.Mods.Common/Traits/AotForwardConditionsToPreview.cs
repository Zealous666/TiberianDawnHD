#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Forwards an actor's CURRENTLY granted conditions into the actor-preview init pipeline, so that
 * previews built from a live actor (most importantly the carried unit drawn by a Carryall) can pick
 * the condition-appropriate sprite body instead of always falling back to the EnabledByDefault one.
 *
 * Background: RenderSprites builds previews by calling RenderPreviewSprites on every sprite-body
 * trait; the stock guard there is `if (!EnabledByDefault) yield break;`, which only ever knows the
 * static default state. For an actor with condition-gated bodies (e.g. the MCV's age0/age1/age2
 * variants) that means a preview always shows the default (age0) body -- visible as a Mammoth-age
 * MCV reverting to its RA look the moment a Carryall picks it up.
 *
 * This trait injects an AotPreviewConditionsInit carrying the live conditions; WithSpriteBody /
 * WithFacingSpriteBody consult it (see WithSpriteBodyInfo.EnabledForPreview) and evaluate their real
 * RequiresCondition against it. When the init is absent (placement previews, actors without this
 * trait) behaviour is unchanged: they fall back to EnabledByDefault exactly as before.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// Transient preview-only init: carries the live actor conditions. Never serialized (previews are
	// not saved), so Save() returns an empty node.
	public sealed class AotPreviewConditionsInit : ActorInit, ISingleInstanceInit
	{
		public readonly IReadOnlyDictionary<string, int> Value;

		public AotPreviewConditionsInit(IReadOnlyDictionary<string, int> value)
		{
			Value = value;
		}

		public override MiniYaml Save() { return new MiniYaml(""); }
	}

	[Desc("aotmod: forwards the actor's currently granted conditions into its actor previews so that " +
		"condition-gated sprite bodies (e.g. age variants) render correctly when carried by a Carryall.")]
	public class AotForwardConditionsToPreviewInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotForwardConditionsToPreview(); }
	}

	public class AotForwardConditionsToPreview : IActorPreviewInitModifier
	{
		void IActorPreviewInitModifier.ModifyActorPreviewInit(Actor self, TypeDictionary inits)
		{
			if (!inits.Contains<AotPreviewConditionsInit>())
				inits.Add(new AotPreviewConditionsInit(self.Conditions));
		}
	}
}
