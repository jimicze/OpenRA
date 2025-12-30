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

using System;
using System.Collections.Generic;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	sealed class ProductionQueueFromSelectionInfo : TraitInfo
	{
		public readonly string ProductionTabsWidget = null;
		public readonly string ProductionPaletteWidget = null;

		public override object Create(ActorInitializer init) { return new ProductionQueueFromSelection(init.World, this); }
	}

	sealed class ProductionQueueFromSelection : INotifySelection
	{
		readonly World world;
		readonly Lazy<ProductionTabsWidget> tabsWidget;
		readonly Lazy<ProductionPaletteWidget> paletteWidget;

		public ProductionQueueFromSelection(World world, ProductionQueueFromSelectionInfo info)
		{
			this.world = world;

			tabsWidget = Exts.Lazy(() => Ui.Root.GetOrNull(info.ProductionTabsWidget) as ProductionTabsWidget);
			paletteWidget = Exts.Lazy(() => Ui.Root.GetOrNull(info.ProductionPaletteWidget) as ProductionPaletteWidget);
		}

		void INotifySelection.SelectionChanged()
		{
			// Disable for spectators
			if (world.LocalPlayer == null)
				return;

			// PERF: Manual iteration with early exit instead of LINQ chains
			ProductionQueue queue = null;

			// Queue-per-actor: find first enabled ProductionQueue on selected actors
			foreach (var a in world.Selection.Actors)
			{
				if (!a.IsInWorld || a.Owner != world.LocalPlayer)
					continue;

				foreach (var q in a.TraitsImplementing<ProductionQueue>())
				{
					if (q.Enabled)
					{
						queue = q;
						break;
					}
				}

				if (queue != null)
					break;
			}

			// Queue-per-player: collect production types and find matching queue
			if (queue == null)
			{
				var types = new HashSet<string>();
				foreach (var a in world.Selection.Actors)
				{
					if (!a.IsInWorld || a.Owner != world.LocalPlayer)
						continue;

					foreach (var p in a.TraitsImplementing<Production>())
					{
						if (!p.IsTraitDisabled)
						{
							foreach (var type in p.Info.Produces)
								types.Add(type);
						}
					}
				}

				if (types.Count > 0)
				{
					foreach (var q in world.LocalPlayer.PlayerActor.TraitsImplementing<ProductionQueue>())
					{
						if (q.Enabled && types.Contains(q.Info.Type))
						{
							queue = q;
							break;
						}
					}
				}
			}

			if (queue == null || (!queue.AnyItemsToBuild() && !queue.AlwaysVisible))
				return;

			if (tabsWidget.Value != null)
				tabsWidget.Value.CurrentQueue = queue;
			else if (paletteWidget.Value != null)
				paletteWidget.Value.CurrentQueue = queue;
		}
	}
}
