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
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Orders;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Orders
{
	public class UnitOrderGenerator : IOrderGenerator
	{
		readonly string worldSelectCursor = ChromeMetrics.Get<string>("WorldSelectCursor");
		readonly string worldDefaultCursor = ChromeMetrics.Get<string>("WorldDefaultCursor");

		protected static Target TargetForInput(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var actor = world.ScreenMap.ActorsAtMouse(mi)
				.Where(a => !a.Actor.IsDead && a.Actor.Info.HasTraitInfo<ITargetableInfo>() && !world.FogObscures(a.Actor))
				.WithHighestSelectionPriority(worldPixel, mi.Modifiers);

			if (actor != null)
				return Target.FromActor(actor);

			var frozen = world.ScreenMap.FrozenActorsAtMouse(world.RenderPlayer, mi)
				.Where(a => a.Info.HasTraitInfo<ITargetableInfo>() && a.Visible && a.HasRenderables)
				.WithHighestSelectionPriority(worldPixel, mi.Modifiers);

			if (frozen != null)
				return Target.FromFrozenActor(frozen);

			return Target.FromCell(world, cell);
		}

		// PERF: Reusable collections to avoid per-click allocations.
		// These are only accessed from the main game thread, so no synchronization needed.
		static readonly List<UnitOrderResult> OrdersBuffer = new(64);
		static readonly HashSet<Actor> ActorsInvolvedSet = new(64);
		static readonly List<Actor> ActorsInvolvedBuffer = new(64);

		public virtual IEnumerable<Order> Order(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var target = TargetForInput(world, cell, worldPixel, mi);

			// PERF: Use static buffers instead of LINQ .Select().Where().ToList()
			OrdersBuffer.Clear();
			foreach (var a in world.Selection.Actors)
			{
				var order = OrderForUnit(a, target, cell, mi);
				if (order != null)
					OrdersBuffer.Add(order);
			}

			if (OrdersBuffer.Count == 0)
				yield break;

			// PERF: Use HashSet for distinct check instead of LINQ .Select().Distinct().ToArray()
			ActorsInvolvedSet.Clear();
			ActorsInvolvedBuffer.Clear();
			foreach (var o in OrdersBuffer)
			{
				if (ActorsInvolvedSet.Add(o.Actor))
					ActorsInvolvedBuffer.Add(o.Actor);
			}

			// HACK: This is required by the hacky player actions-per-minute calculation
			// TODO: Reimplement APM properly and then remove this
			yield return new Order("CreateGroup", ActorsInvolvedBuffer[0].Owner.PlayerActor, false, ActorsInvolvedBuffer.ToArray());

			foreach (var o in OrdersBuffer)
				yield return CheckSameOrder(o.Order, o.Trait.IssueOrder(o.Actor, o.Order, o.Target, mi.Modifiers.HasModifier(Modifiers.Shift)));
		}

		public virtual void Tick(World world) { }
		public virtual IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
		public virtual IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }
		public virtual IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world) { yield break; }

		public virtual string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var target = TargetForInput(world, cell, worldPixel, mi);

			bool useSelect;
			if (Game.Settings.Game.UseClassicMouseStyle && !InputOverridesSelection(world, worldPixel, mi))
				useSelect = target.Type == TargetType.Actor && target.Actor.Info.HasTraitInfo<ISelectableInfo>();
			else
			{
				// PERF: Replace LINQ .Select().Where().MaxByOrDefault() with manual iteration
				UnitOrderResult bestCursorOrder = null;
				var bestPriority = int.MinValue;
				foreach (var a in world.Selection.Actors)
				{
					var order = OrderForUnit(a, target, cell, mi);
					if (order != null && order.Cursor != null && order.Order.OrderPriority > bestPriority)
					{
						bestCursorOrder = order;
						bestPriority = order.Order.OrderPriority;
					}
				}

				if (bestCursorOrder != null)
					return bestCursorOrder.Cursor;

				useSelect = target.Type == TargetType.Actor && target.Actor.Info.HasTraitInfo<ISelectableInfo>() &&
					(mi.Modifiers.HasModifier(Modifiers.Shift) || world.Selection.Actors.Count == 0);
			}

			return useSelect ? worldSelectCursor : worldDefaultCursor;
		}

		public void Deactivate() { }

		bool IOrderGenerator.HandleKeyPress(KeyInput e) { return false; }

		// Used for classic mouse orders, determines whether or not action at xy is move or select
		public virtual bool InputOverridesSelection(World world, int2 xy, MouseInput mi)
		{
			var actor = world.ScreenMap.ActorsAtMouse(xy)
				.Where(a =>
					!a.Actor.IsDead &&
					a.Actor.Info.HasTraitInfo<ISelectableInfo>() &&
					(a.Actor.Owner.IsAlliedWith(world.RenderPlayer) || !world.FogObscures(a.Actor)))
				.WithHighestSelectionPriority(xy, mi.Modifiers);

			if (actor == null)
				return true;

			var target = Target.FromActor(actor);
			var cell = world.Map.CellContaining(target.CenterPosition);
			var actorsAt = world.ActorMap.GetActorsAt(cell).ToList();

			var modifiers = TargetModifiers.None;
			if (mi.Modifiers.HasModifier(Modifiers.Ctrl))
				modifiers |= TargetModifiers.ForceAttack;
			if (mi.Modifiers.HasModifier(Modifiers.Shift))
				modifiers |= TargetModifiers.ForceQueue;
			if (mi.Modifiers.HasModifier(Modifiers.Alt))
				modifiers |= TargetModifiers.ForceMove;

			foreach (var a in world.Selection.Actors)
			{
				var o = OrderForUnit(a, target, cell, mi);
				if (o != null && o.Order.TargetOverridesSelection(a, target, actorsAt, cell, modifiers))
					return true;
			}

			return false;
		}

		public virtual void SelectionChanged(World world, IEnumerable<Actor> selected) { }

		/// <summary>
		/// Returns the most appropriate order for a given actor and target.
		/// First priority is given to orders that interact with the given actors.
		/// Second priority is given to actors in the given cell.
		/// </summary>
		protected static UnitOrderResult OrderForUnit(Actor self, Target target, CPos xy, MouseInput mi)
		{
			if (mi.Button != Game.Settings.Game.MouseButtonPreference.Action)
				return null;

			if (self.Owner != self.World.LocalPlayer)
				return null;

			if (self.World.IsGameOver)
				return null;

			if (self.Disposed || !target.IsValidFor(self))
				return null;

			var modifiers = TargetModifiers.None;
			if (mi.Modifiers.HasModifier(Modifiers.Ctrl))
				modifiers |= TargetModifiers.ForceAttack;
			if (mi.Modifiers.HasModifier(Modifiers.Shift))
				modifiers |= TargetModifiers.ForceQueue;
			if (mi.Modifiers.HasModifier(Modifiers.Alt))
				modifiers |= TargetModifiers.ForceMove;

			// PERF: Use pre-cached IIssueOrder traits from Actor.
			// Query Orders dynamically to respect current enabled state of conditional traits.
			// Track best match by priority instead of sorting to avoid allocations.
			var issueOrderTraits = self.IssueOrderTraits;

			for (var i = 0; i < 2; i++)
			{
				UnitOrderResult bestResult = null;
				var bestPriority = int.MinValue;

				foreach (var trait in issueOrderTraits)
				{
					foreach (var order in trait.Orders)
					{
						if (order.OrderPriority <= bestPriority)
							continue;

						var localModifiers = modifiers;
						string cursor = null;
						if (order.CanTarget(self, target, ref localModifiers, ref cursor))
						{
							bestResult = new UnitOrderResult(self, order, trait, cursor, target);
							bestPriority = order.OrderPriority;
						}
					}
				}

				if (bestResult != null)
					return bestResult;

				// No valid orders, so check for orders against the cell
				target = Target.FromCell(self.World, xy);
			}

			return null;
		}

		static Order CheckSameOrder(IOrderTargeter iot, Order order)
		{
			if (order == null && iot.OrderID != null)
				TextNotificationsManager.Debug("BUG: in order targeter - decided on {0} but then didn't order", iot.OrderID);
			else if (order != null && iot.OrderID != order.OrderString)
				TextNotificationsManager.Debug("BUG: in order targeter - decided on {0} but ordered {1}", iot.OrderID, order.OrderString);
			return order;
		}

		protected sealed class UnitOrderResult(Actor actor, IOrderTargeter order, IIssueOrder trait, string cursor, in Target target)
		{
			public readonly Actor Actor = actor;
			public readonly IOrderTargeter Order = order;
			public readonly IIssueOrder Trait = trait;
			public readonly string Cursor = cursor;
			public ref readonly Target Target => ref target;

			readonly Target target = target;
		}

		public virtual bool ClearSelectionOnLeftClick => true;
	}
}
