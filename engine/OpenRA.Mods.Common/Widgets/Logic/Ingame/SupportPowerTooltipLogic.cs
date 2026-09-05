#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

// === Age of Tiberium (aotmod) ===
// Layout auf das Muster des Bau-Menue-Tooltips gebracht (User 2026-08-04): links Name,
// Voraussetzungen und Beschreibung, rechts eine Spalte aus Preis und Ladezeit mit Icons.
// Vorher stand nur die Zeit oben rechts, Preis und Voraussetzungen gab es gar nicht -- als
// Fliesstext in der Description ging beides nicht, weil Tooltips keine Zeilen umbrechen.
// Die Rechenschritte sind bewusst dieselben wie in ProductionTooltipLogic, damit beide
// Tooltips identisch aussehen und sich bei Aenderungen gleich verhalten.
// === Ende aotmod ===

using System;
using System.Globalization;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class SupportPowerTooltipLogic : ChromeLogic
	{
		// Dieselbe Fluent-Zeile wie ProductionTooltipLogic: "Requires { $prerequisites }."
		const string Requires = "label-requires";

		[ObjectCreator.UseCtor]
		public SupportPowerTooltipLogic(Widget widget, TooltipContainerWidget tooltipContainer,
			Func<SupportPowersWidget.SupportPowerIcon> getTooltipIcon, World world)
		{
			widget.IsVisible = () => getTooltipIcon() != null && getTooltipIcon().Power.Info != null;
			var nameLabel = widget.Get<LabelWidget>("NAME");
			var hotkeyLabel = widget.Get<LabelWidget>("HOTKEY");
			var timeLabel = widget.Get<LabelWidget>("TIME");
			var descLabel = widget.Get<LabelWidget>("DESC");

			// Optional, damit Tooltip-Templates ohne diese Felder weiter funktionieren.
			var requiresLabel = widget.GetOrNull<LabelWidget>("REQUIRES");
			var costLabel = widget.GetOrNull<LabelWidget>("COST");
			var costIcon = widget.GetOrNull<ImageWidget>("COST_ICON");
			var timeIcon = widget.GetOrNull<ImageWidget>("TIME_ICON");

			var font = Game.Renderer.Fonts[nameLabel.Font];
			var hotkeyFont = Game.Renderer.Fonts[hotkeyLabel.Font];
			var timeFont = Game.Renderer.Fonts[timeLabel.Font];
			var descFont = Game.Renderer.Fonts[descLabel.Font];
			var requiresFont = requiresLabel != null ? Game.Renderer.Fonts[requiresLabel.Font] : null;
			var costFont = costLabel != null ? Game.Renderer.Fonts[costLabel.Font] : null;

			// Kassenstand des ZUSCHAUENDEN Spielers -- damit der Preis rot wird, wenn er ihn sich
			// gerade nicht leisten kann (User 2026-08-04, wie im Bau-Menue). Beobachter und Replays
			// haben keinen LocalPlayer, deshalb TraitOrDefault und Null-Pruefung im Delegaten.
			var playerResources = world.LocalPlayer?.PlayerActor.TraitOrDefault<PlayerResources>();
			var techTree = world.LocalPlayer?.PlayerActor.TraitOrDefault<TechTree>();

			// Ausgangsgeometrie merken: die Zeilen der rechten Spalte wandern je nachdem, ob es
			// ueberhaupt einen Preis gibt -- es darf also nicht auf die laufend ueberschriebenen
			// Bounds zurueckgegriffen werden.
			var iconMargin = timeIcon?.Bounds.X ?? 0;
			var descLabelY = descLabel.Bounds.Y;
			var descLabelPadding = descLabel.Bounds.Height;
			var row1IconY = costIcon?.Bounds.Y ?? 0;
			var row1LabelY = costLabel?.Bounds.Y ?? 0;
			var row2IconY = timeIcon?.Bounds.Y ?? 0;
			var row2LabelY = timeLabel.Bounds.Y;

			SupportPowerInstance lastPower = null;
			var lastHotkey = Hotkey.Invalid;
			var lastRemainingSeconds = 0;

			tooltipContainer.BeforeRender = () =>
			{
				var icon = getTooltipIcon();
				if (icon == null || icon.Power == null || icon.Power.Instances.Count == 0)
					return;

				var sp = icon.Power;

				// HACK: This abuses knowledge of the internals of WidgetUtils.FormatTime
				// to efficiently work when the label is going to change, requiring a panel relayout
				var remainingSeconds = (int)Math.Ceiling(sp.RemainingTicks * world.Timestep / 1000f);

				var hotkey = icon.Hotkey?.GetValue() ?? Hotkey.Invalid;
				if (sp == lastPower && hotkey == lastHotkey && lastRemainingSeconds == remainingSeconds)
					return;

				nameLabel.GetText = () => sp.Name;
				var nameSize = font.Measure(sp.Name);

				var hotkeyWidth = 0;
				hotkeyLabel.Visible = hotkey.IsValid();
				if (hotkeyLabel.Visible)
				{
					var hotkeyText = $"({hotkey.DisplayString()})";
					hotkeyWidth = hotkeyFont.Measure(hotkeyText).X + 2 * nameLabel.Bounds.X;
					hotkeyLabel.GetText = () => hotkeyText;
					hotkeyLabel.Bounds.X = nameSize.X + 2 * nameLabel.Bounds.X;
				}

				// Voraussetzungen schieben die Beschreibung eine Zeile nach unten -- exakt wie im
				// Bau-Menue-Tooltip, wo dieselbe Zeile zwischen Name und Beschreibung sitzt.
				var requiresSize = int2.Zero;
				if (requiresLabel != null)
				{
					var requirement = sp.Info.TooltipRequirements;
					requiresLabel.Visible = !string.IsNullOrEmpty(requirement);
					if (requiresLabel.Visible)
					{
						// aotmod 2026-08-05: noch nicht erfuellte Voraussetzung rot (User-Wunsch),
						// gleiche Mechanik wie im Bau-Menue-Tooltip -- LabelWithHighlightWidget faerbt
						// den Text zwischen '<' und '>'.
						//
						// Eingeklammert wird NUR der Name, nie die ganze Zeile: das Widget prueft
						// "highlightStart > 0" (LabelWithHighlightWidget.cs), ein '<' an Position 0
						// wird also gar nicht als Markierung erkannt und landet roh im Tooltip.
						// Das "Requires ..."-Geruest kommt deshalb aus derselben Fluent-Zeile wie im
						// Bau-Menue -- gleiche Formulierung, gleiche Uebersetzung, und der Name steht
						// garantiert nicht am Zeilenanfang.
						var prereqs = sp.Info.TooltipRequirementsPrerequisites;
						if (prereqs.Length > 0 && techTree != null && !techTree.HasPrerequisites(prereqs))
							requirement = $"<{requirement}>";

						var requiresText = FluentProvider.GetMessage(Requires, "prerequisites", requirement);
						requiresLabel.GetText = () => requiresText;
						requiresSize = requiresFont.Measure(requiresText);
						descLabel.Bounds.Y = descLabelY + requiresLabel.Bounds.Height;
					}
					else
						descLabel.Bounds.Y = descLabelY;
				}

				var desc = sp.Description ?? "";
				descLabel.GetText = () => desc;
				var descSize = descFont.Measure(desc);
				descLabel.Bounds.Width = descSize.X;
				descLabel.Bounds.Height = descSize.Y + descLabelPadding;

				// Nur die RESTZEIT (User 2026-08-04). Der Engine-Default war "Rest / Gesamt" -- im
				// Bau-Menue steht an derselben Stelle ebenfalls nur ein Wert, und die Gesamtdauer
				// sagt waehrend des Ladens ohnehin nichts, was der Countdown nicht schon zeigt.
				var timeText = sp.TooltipTimeTextOverride()
					?? WidgetUtils.FormatTime(sp.RemainingTicks, world.Timestep);

				timeLabel.GetText = () => timeText;
				var timeSize = timeFont.Measure(timeText);

				// Preis nur zeigen, wenn die Power ueberhaupt einen hat. Faellt er weg, rueckt die
				// Zeit auf die erste Zeile hoch, damit rechts kein leerer Platz stehen bleibt.
				var costSize = int2.Zero;
				var showCost = costLabel != null && sp.Info.Cost > 0;
				if (costLabel != null)
				{
					costLabel.Visible = showCost;
					if (costIcon != null)
						costIcon.Visible = showCost;

					if (showCost)
					{
						var cost = sp.Info.Cost;
						var costText = cost.ToString(NumberFormatInfo.CurrentInfo);
						costLabel.GetText = () => costText;

						// Als Delegat, nicht als fester Wert: BeforeRender steigt oben frueh aus,
						// solange sich Power/Hotkey/Sekunde nicht aendern -- eine hier einmalig
						// gesetzte Farbe bliebe also stehen, waehrend das Geld weiterlaeuft.
						costLabel.GetColor = () => playerResources == null
							|| playerResources.GetCashAndResources() >= cost ? Color.White : Color.Red;

						costSize = costFont.Measure(costText);
					}
				}

				if (showCost)
				{
					if (costIcon != null)
						costIcon.Bounds.Y = row1IconY;

					costLabel.Bounds.Y = row1LabelY;

					if (timeIcon != null)
						timeIcon.Bounds.Y = row2IconY;

					timeLabel.Bounds.Y = row2LabelY;
				}
				else
				{
					if (timeIcon != null)
						timeIcon.Bounds.Y = row1IconY;

					timeLabel.Bounds.Y = row1LabelY;
				}

				var leftWidth = new[] { nameSize.X + hotkeyWidth, requiresSize.X, descSize.X }.Aggregate(Math.Max);
				var rightWidth = Math.Max(timeSize.X, costSize.X);
				var iconWidth = timeIcon?.Bounds.Width ?? 0;
				var iconX = leftWidth + 2 * nameLabel.Bounds.X;

				if (timeIcon != null)
					timeIcon.Bounds.X = iconX;

				if (costIcon != null)
					costIcon.Bounds.X = iconX;

				var labelX = iconX + iconWidth + iconMargin;
				timeLabel.Bounds.X = labelX;
				if (costLabel != null)
					costLabel.Bounds.X = labelX;

				widget.Bounds.Width = leftWidth + rightWidth + 3 * nameLabel.Bounds.X + iconWidth + iconMargin;

				// Unterer Rand = linker bzw. oberer Rand, wie im Bau-Menue-Tooltip.
				var leftHeight = descLabel.Bounds.Bottom + descLabel.Bounds.X;
				var rightHeight = (timeIcon != null ? timeIcon.Bounds.Bottom : timeLabel.Bounds.Bottom) + row1IconY;

				widget.Bounds.Height = Math.Max(leftHeight, rightHeight);

				lastPower = sp;
				lastHotkey = hotkey;
				lastRemainingSeconds = remainingSeconds;
			};

			timeLabel.GetColor = () => getTooltipIcon() != null && !getTooltipIcon().Power.Active
				? Color.Red : Color.White;
		}
	}
}
