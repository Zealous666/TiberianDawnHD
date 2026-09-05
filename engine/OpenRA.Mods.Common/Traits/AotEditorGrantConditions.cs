#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Map editor checkbox that, when ticked, grants a fixed set of conditions once at creation.
 * Used on the GDI Orbital Command so a mapper can decide per placed building which support
 * powers it carries (Ion Cannon Strike, Zone Trooper Drop, Orca Transport). The conditions it
 * grants are the very same ones the upgrade purchases grant in normal play, so ticking a box
 * both enables the power (transferred to whoever owns the building -- so capturing a neutral
 * Orbital Command hands the capturer the power temporarily, like other neutral tech buildings)
 * and lights up the matching upgrade socket sprite. Multiple instances per actor are supported;
 * tag each with an @instanceName so every checkbox stores its own choice in map.yaml.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Map editor checkbox that grants a set of conditions once, at creation, when ticked.",
		"Everything that should react (a support power via RequiresCondition, an upgrade overlay",
		"sprite, an age-body switch) is wired up in rules against these conditions -- this trait",
		"only decides whether they are granted. Supports multiple instances per actor when each",
		"is tagged with an @instanceName.")]
	public class AotEditorGrantConditionsInfo : TraitInfo, IEditorActorOptions
	{
		[FieldLoader.Require]
		[Desc("Label shown next to the checkbox in the map editor.")]
		public readonly string Label = null;

		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Conditions granted once at creation while the checkbox is ticked.")]
		public readonly string[] Conditions = null;

		[Desc("Whether the checkbox starts ticked.")]
		public readonly bool Enabled = false;

		[Desc("Display order for the checkbox in the map editor.")]
		public readonly int EditorDisplayOrder = 1;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox(Label, EditorDisplayOrder,
				actor => actor.GetInitOrDefault<AotEditorGrantConditionsInit>(this)?.Value ?? Enabled,
				(actor, value) => actor.ReplaceInit(new AotEditorGrantConditionsInit(this, value), this));
		}

		public override object Create(ActorInitializer init) { return new AotEditorGrantConditions(init, this); }
	}

	public class AotEditorGrantConditions : INotifyCreated
	{
		readonly AotEditorGrantConditionsInfo info;
		readonly bool enabled;

		public AotEditorGrantConditions(ActorInitializer init, AotEditorGrantConditionsInfo info)
		{
			this.info = info;
			enabled = init.GetValue<AotEditorGrantConditionsInit, bool>(info, info.Enabled);
		}

		void INotifyCreated.Created(Actor self)
		{
			if (!enabled)
				return;

			foreach (var condition in info.Conditions)
				self.GrantCondition(condition);
		}
	}

	public class AotEditorGrantConditionsInit : ValueActorInit<bool>
	{
		public AotEditorGrantConditionsInit(TraitInfo info, bool value)
			: base(info, value) { }

		public AotEditorGrantConditionsInit(string instanceName, bool value)
			: base(instanceName, value) { }

		public AotEditorGrantConditionsInit(bool value)
			: base(value) { }
	}
}
