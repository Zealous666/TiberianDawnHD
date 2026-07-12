#region Copyright & License Information
/*
 * Age of Tiberium mod — distinct NukePower subtypes so multiple independently-gated
 * nuke-style powers can coexist on the same actor (e.g. MSLO's Tiberium Missile and
 * Atom Bomb). SupportPowerInfo.OrderName is derived from the Info class's runtime type
 * (GetType().Name), so multiple `NukePower@key:` instances on one actor all resolve to
 * the identical OrderName and collide in SupportPowerManager.Powers — only one of them
 * ever shows up in the support power palette. Subclassing NukePowerInfo (not sealed)
 * gives each variant its own OrderName without touching the sealed NukePower behavior
 * class or duplicating any logic.
 */
#endregion

namespace OpenRA.Mods.Common.Traits
{
	public class AotTiberiumMissilePowerInfo : NukePowerInfo { }

	public class AotAtomBombPowerInfo : NukePowerInfo { }

	public class AotMineClusterEnhancedPowerInfo : NukePowerInfo { }
}
