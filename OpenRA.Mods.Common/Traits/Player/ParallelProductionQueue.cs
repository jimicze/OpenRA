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

using System.Collections.Generic;

namespace OpenRA.Mods.Common.Traits
{
	public class ParallelProductionQueueInfo : ProductionQueueInfo
	{
		public override object Create(ActorInitializer init) { return new ParallelProductionQueue(init, this); }
	}

	public class ParallelProductionQueue : ProductionQueue
	{
		public ParallelProductionQueue(ActorInitializer init, ParallelProductionQueueInfo info)
			: base(init, info) { }

		protected override void TickInner(Actor self, bool allProductionPaused)
		{
			CancelUnbuildableItems();

			// PERF: Manual loop instead of Queue.FirstOrDefault(i => !i.Paused)
			ProductionItem item = null;
			foreach (var i in Queue)
			{
				if (!i.Paused)
				{
					item = i;
					break;
				}
			}

			if (item == null || allProductionPaused)
				return;

			var before = item.RemainingTime;
			item.Tick(playerResources);

			if (item.RemainingTime == before)
				return;

			// PERF: Collect matching items first, then modify queue
			// (can't modify during enumeration)
			var itemsToMove = new List<ProductionItem>();
			foreach (var other in Queue)
			{
				if (other.Item == item.Item)
					itemsToMove.Add(other);
			}

			// As we have progressed this actor type, we will move all queued items of this actor to the end.
			foreach (var other in itemsToMove)
			{
				Queue.Remove(other);
				Queue.Add(other);
			}
		}

		public override bool IsProducing(ProductionItem item)
		{
			return Queue.Contains(item);
		}

		protected override void BeginProduction(ProductionItem item, bool hasPriority)
		{
			// Ignore `hasPriority` as it's not relevant in parallel production context.
			base.BeginProduction(item, false);
		}

		protected override void PauseProduction(string itemName, bool paused)
		{
			// PERF: Manual loop instead of Queue.Where()
			foreach (var item in Queue)
			{
				if (item.Item == itemName)
					item.Pause(paused);
			}
		}

		public override int RemainingTimeActual(ProductionItem item)
		{
			// PERF: Use HashSet to count distinct items instead of GroupBy().ToList().Count
			var distinctItems = new HashSet<string>();
			foreach (var i in Queue)
			{
				if (!i.Paused && !i.Done)
					distinctItems.Add(i.Item);
			}

			return item.RemainingTimeActual * distinctItems.Count;
		}
	}
}
