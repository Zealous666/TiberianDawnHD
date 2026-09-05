#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Map editor checkbox that decides whether a placed building participates in the power economy
 * at all, or stands completely independent of it (used by ATWR/aot-tesla so they can serve both
 * as a normal Buildable defense and as an editor-only decoration on maps without a power grid).
 */
#endregion

using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Grants a condition once, at creation, based on a map editor checkbox. Everything that",
		"should differ between \"needs power\" and \"power-independent\" (Power, the low-power",
		"penalties, the disabled overlay) is wired up in rules via RequiresCondition on this",
		"trait's Condition -- this trait itself only decides whether that condition is granted.")]
	public class AotOptionalPowerInfo : TraitInfo, IEditorActorOptions
	{
		[Desc("CHECKBOX: whether the placed actor needs power (normal behaviour) or is completely",
			"independent of the power economy.")]
		public readonly bool RequiresPower = true;

		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Condition granted once at creation while RequiresPower is true.")]
		public readonly string Condition = null;

		[Desc("Display order for the checkbox in the map editor.")]
		public readonly int EditorDisplayOrder = 1;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox("Requires power", EditorDisplayOrder,
				actor => actor.GetInitOrDefault<AotOptionalPowerInit>(this)?.Value ?? RequiresPower,
				(actor, value) => actor.ReplaceInit(new AotOptionalPowerInit(value)));
		}

		public override object Create(ActorInitializer init) { return new AotOptionalPower(init, this); }
	}

	public class AotOptionalPower : INotifyCreated
	{
		readonly AotOptionalPowerInfo info;
		readonly bool requiresPower;

		public AotOptionalPower(ActorInitializer init, AotOptionalPowerInfo info)
		{
			this.info = info;
			requiresPower = init.GetValue<AotOptionalPowerInit, bool>(info.RequiresPower);
		}

		void INotifyCreated.Created(Actor self)
		{
			if (requiresPower)
				self.GrantCondition(info.Condition);
		}
	}

	public class AotOptionalPowerInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotOptionalPowerInit(bool value)
			: base(value) { }
	}
}
