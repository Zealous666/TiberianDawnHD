// === Age of Tiberium (aotmod) ===
// Wires the Age.GDI or Age.Nod production queue (hosted on the Player actor) to a
// standalone ProductionPaletteWidget positioned left of the minimap.
// The queue is always enabled (Player actor lives from game start), so the widget
// appears immediately without requiring any building.
// Faction split: only the queue matching the local player's faction is Enabled.
// === Ende aotmod ===

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class AotAgeProductionLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public AotAgeProductionLogic(Widget widget, World world)
		{
			var palette = widget.Get<ProductionPaletteWidget>("AGE_PALETTE");

			if (world.LocalPlayer == null)
			{
				widget.IsVisible = () => false;
				return;
			}

			var queue = world.LocalPlayer.PlayerActor
				.TraitsImplementing<ProductionQueue>()
				.FirstOrDefault(q => (q.Info.Group ?? q.Info.Type) == "Age" && q.Enabled);

			palette.CurrentQueue = queue;

			if (queue == null)
				widget.IsVisible = () => false;
		}
	}
}
