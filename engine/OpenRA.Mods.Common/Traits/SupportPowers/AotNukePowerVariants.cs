#region Copyright & License Information
/*
 * Age of Tiberium mod — distinct NukePower subtype(s) so a stock vertical nuke-style power
 * (MSLO's Atom Bomb) can coexist on the same actor as the flying cluster-missile powers
 * (see AotClusterMissilePower). SupportPowerInfo.OrderName is derived from the Info class's
 * runtime type (GetType().Name), so multiple `NukePower@key:` instances on one actor would
 * all resolve to the identical OrderName and collide in SupportPowerManager.Powers — only
 * one would ever show up in the support power palette. Subclassing NukePowerInfo (not
 * sealed) gives the variant its own OrderName without touching the sealed NukePower
 * behavior class or duplicating any logic.
 */
#endregion

namespace OpenRA.Mods.Common.Traits
{
	public class AotAtomBombPowerInfo : NukePowerInfo { }
}
