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
using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Attach this to the player actor (not a building!) to define a new shared build queue.",
		"Will only work together with the Production: trait on the actor that actually does the production.",
		"You will also want to add PrimaryBuildings: to let the user choose where new units should exit.",
		"The production speed depends on the number of production buildings and units queued at the same time.")]
	[TraitLocation(SystemActors.Player)]
	public class ClassicParallelProductionQueueInfo : ProductionQueueInfo, Requires<TechTreeInfo>, Requires<PlayerResourcesInfo>
	{
		[Desc("If you build more actors of the same type,", "the same queue will get its build time lowered for every actor produced there.")]
		public readonly bool SpeedUp = false;

		[Desc("Every time another production building of the same queue is",
			"constructed, the build times of all actors in the queue",
			"modified by a percentage of the original time.")]
		public readonly int[] BuildingCountBuildTimeMultipliers = [100, 86, 75, 67, 60, 55, 50];

		[Desc("Build time modifier multiplied by the number of parallel production for producing different actors at the same time.")]
		public readonly int[] ParallelPenaltyBuildTimeMultipliers = [100, 116, 133, 150, 166, 183, 200, 216, 233, 250];

		public override object Create(ActorInitializer init) { return new ClassicParallelProductionQueue(init, this); }
	}

	public class ClassicParallelProductionQueue : ProductionQueue
	{
		static readonly ActorInfo[] NoItems = [];

		readonly Actor self;
		readonly ClassicParallelProductionQueueInfo info;

		int penalty;

		public ClassicParallelProductionQueue(ActorInitializer init, ClassicParallelProductionQueueInfo info)
			: base(init, info)
		{
			self = init.Self;
			this.info = info;
		}

		protected override void Tick(Actor self)
		{
			// PERF: Avoid LINQ.
			Enabled = false;
			var isActive = false;
			foreach (var x in self.World.ActorsWithTrait<Production>())
			{
				if (x.Trait.IsTraitDisabled)
					continue;

				if (x.Actor.Owner != self.Owner || !x.Trait.Info.Produces.Contains(Info.Type))
					continue;

				Enabled |= IsValidFaction;
				isActive |= !x.Trait.IsTraitPaused;
			}

			if (!Enabled)
				ClearQueue();

			TickInner(self, !isActive);
		}

		protected override void TickInner(Actor self, bool allProductionPaused)
		{
			CancelUnbuildableItems();

			if (allProductionPaused)
				return;

			var item = Queue.FirstOrDefault(i => !i.Paused);
			if (item == null)
				return;

			// PERF: Use HashSet to count distinct items instead of GroupBy().ToList().Count
			// This avoids allocating IGrouping objects and intermediate lists
			var distinctItems = new HashSet<string>();
			foreach (var i in Queue)
			{
				if (!i.Paused && !i.Done)
					distinctItems.Add(i.Item);
			}

			var parallelBuilds = distinctItems.Count - 1;

			if (parallelBuilds > 0 && !developerMode.FastBuild)
			{
				penalty -= 100;
				if (penalty < 0)
					penalty = info.ParallelPenaltyBuildTimeMultipliers[Math.Min(parallelBuilds, info.ParallelPenaltyBuildTimeMultipliers.Length - 1)];
				else
					return;
			}

			var before = item.RemainingTime;
			item.Tick(playerResources);

			if (item.RemainingTime == before)
				return;

			// As we have progressed this actor type, we will move all queued items of this actor to the end.
			foreach (var other in Queue.FindAll(a => a.Item == item.Item))
			{
				Queue.Remove(other);
				Queue.Add(other);
			}
		}

		public override IEnumerable<ActorInfo> AllItems()
		{
			return Enabled ? base.AllItems() : NoItems;
		}

		public override IEnumerable<ActorInfo> BuildableItems()
		{
			return Enabled ? base.BuildableItems() : NoItems;
		}

		public override bool IsProducing(ProductionItem item)
		{
			return Queue.Contains(item);
		}

		public override TraitPair<Production> MostLikelyProducer()
		{
			// PERF: Avoid LINQ - single-pass iteration to find best producer
			// Priority: not paused > paused, primary > non-primary, higher ActorID > lower
			TraitPair<Production> best = default;
			var bestIsPaused = true;
			var bestIsPrimary = false;
			var bestActorId = 0u;
			var found = false;

			foreach (var x in self.World.ActorsWithTrait<Production>())
			{
				if (x.Actor.Owner != self.Owner || x.Trait.IsTraitDisabled || !x.Trait.Info.Produces.Contains(Info.Type))
					continue;

				var isPaused = x.Trait.IsTraitPaused;
				var isPrimary = x.Actor.IsPrimaryBuilding();
				var actorId = x.Actor.ActorID;

				// Compare: prefer not paused, then primary, then higher actor ID
				var isBetter = false;
				if (!found)
					isBetter = true;
				else if (isPaused != bestIsPaused)
					isBetter = !isPaused; // Not paused is better
				else if (isPrimary != bestIsPrimary)
					isBetter = isPrimary; // Primary is better
				else if (actorId > bestActorId)
					isBetter = true; // Higher actor ID is better

				if (isBetter)
				{
					best = x;
					bestIsPaused = isPaused;
					bestIsPrimary = isPrimary;
					bestActorId = actorId;
					found = true;
				}
			}

			return best;
		}

		protected override bool BuildUnit(ActorInfo unit)
		{
			// Find a production structure to build this actor
			var bi = BuildableInfo.GetTraitForQueue(unit, Info.Type);

			// Some units may request a specific production type, which is ignored if the AllTech cheat is enabled
			var type = developerMode.AllTech ? Info.Type : (bi.BuildAtProductionType ?? Info.Type);

			// PERF: Avoid LINQ - collect valid producers and sort by priority
			// We need to try producers in order: primary first, then by descending actor ID
			// But we also skip paused producers, so we iterate all and try non-paused ones
			TraitPair<Production> bestProducer = default;
			var bestIsPrimary = false;
			var bestActorId = 0u;
			var anyProducers = false;

			foreach (var x in self.World.ActorsWithTrait<Production>())
			{
				if (x.Actor.Owner != self.Owner || x.Trait.IsTraitDisabled || !x.Trait.Info.Produces.Contains(type))
					continue;

				anyProducers = true;

				// Skip paused producers for now - we want to try active ones first
				if (x.Trait.IsTraitPaused)
					continue;

				var isPrimary = x.Actor.IsPrimaryBuilding();
				var actorId = x.Actor.ActorID;

				// Find best non-paused producer: primary first, then highest actor ID
				var isBetter = false;
				if (bestProducer.Actor == null)
					isBetter = true;
				else if (isPrimary != bestIsPrimary)
					isBetter = isPrimary;
				else if (actorId > bestActorId)
					isBetter = true;

				if (isBetter)
				{
					bestProducer = x;
					bestIsPrimary = isPrimary;
					bestActorId = actorId;
				}
			}

			// Try to produce with the best non-paused producer
			if (bestProducer.Actor != null)
			{
				var inits = new TypeDictionary
				{
					new OwnerInit(self.Owner),
					new FactionInit(BuildableInfo.GetInitialFaction(unit, bestProducer.Trait.Faction))
				};

				var item = Queue.First(i => i.Done && i.Item == unit.Name);
				if (bestProducer.Trait.Produce(bestProducer.Actor, unit, type, inits, item.TotalCost))
				{
					EndProduction(item);
					return true;
				}
			}

			if (!anyProducers)
				CancelProduction(unit.Name, 1);

			return false;
		}

		protected override void BeginProduction(ProductionItem item, bool hasPriority)
		{
			// Ignore `hasPriority` as it's not relevant in parallel production context.
			base.BeginProduction(item, false);
		}

		protected override void PauseProduction(string itemName, bool paused)
		{
			foreach (var item in Queue.Where(a => a.Item == itemName))
				item.Pause(paused);
		}

		public override int GetBuildTime(ActorInfo unit, BuildableInfo bi)
		{
			if (developerMode.FastBuild)
				return 0;

			var time = base.GetBuildTime(unit, bi);

			if (info.SpeedUp)
			{
				var type = bi.BuildAtProductionType ?? info.Type;

				// PERF: Avoid LINQ Count() - manual iteration
				var selfsameProductionsCount = 0;
				foreach (var p in self.World.ActorsWithTrait<Production>())
				{
					if (!p.Trait.IsTraitDisabled && !p.Trait.IsTraitPaused &&
						p.Actor.Owner == self.Owner && p.Trait.Info.Produces.Contains(type))
						selfsameProductionsCount++;
				}

				var speedModifier = selfsameProductionsCount.Clamp(1, info.BuildingCountBuildTimeMultipliers.Length) - 1;
				time = time * info.BuildingCountBuildTimeMultipliers[speedModifier] / 100;
			}

			return time;
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

			var parallelBuilds = distinctItems.Count;
			return item.RemainingTimeActual *
				parallelBuilds *
				info.ParallelPenaltyBuildTimeMultipliers[Math.Min(parallelBuilds - 1, info.ParallelPenaltyBuildTimeMultipliers.Length - 1)] / 100;
		}
	}
}
