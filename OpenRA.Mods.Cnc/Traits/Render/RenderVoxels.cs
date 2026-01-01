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
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits.Render
{
	/// <summary>
	/// Static helper to detect mass voxel visibility changes (potential bug indicator).
	/// </summary>
	public static class VoxelBlinkDetector
	{
		// Thresholds for mass blink detection
		const int MassBlinkThreshold = 20;

		// Per-tick tracking
		static int currentTick = -1;
		static int invisibleCountThisTick;
		static int visibleCountThisTick;
		static readonly List<string> InvisibleActorsThisTick = new(64);

		// Per-frame tracking
		static int currentFrame = -1;
		static int invisibleCountThisFrame;
		static int visibleCountThisFrame;

		// Actor types known to toggle visibility normally (dock animations, conditional upgrades)
		static readonly HashSet<string> ExpectedToggleActors = new(StringComparer.OrdinalIgnoreCase)
		{
			// Miners with dock/undock animations
			"cmin", "gmin", "harv", "smin", "slav",

			// Other actors that commonly toggle (add as needed)
		};

		public static void RecordVisibilityChange(Actor self, bool becameVisible, int worldTick)
		{
			// Reset counters on new tick
			if (worldTick != currentTick)
			{
				// Check for mass blink on previous tick before resetting
				if (currentTick >= 0 && (invisibleCountThisTick >= MassBlinkThreshold || visibleCountThisTick >= MassBlinkThreshold))
				{
					var actorList = string.Join(", ", InvisibleActorsThisTick.Take(10));
					var suffix = InvisibleActorsThisTick.Count > 10 ? "..." : "";
					Log.Write("debug",
						$"[VOXEL-MASS-BLINK-TICK] Tick {currentTick}: {invisibleCountThisTick} actors went INVISIBLE, " +
						$"{visibleCountThisTick} went VISIBLE. Actors: {actorList}{suffix}");
				}

				currentTick = worldTick;
				invisibleCountThisTick = 0;
				visibleCountThisTick = 0;
				InvisibleActorsThisTick.Clear();
			}

			if (becameVisible)
				visibleCountThisTick++;
			else
			{
				invisibleCountThisTick++;
				InvisibleActorsThisTick.Add($"{self.ActorID}({self.Info.Name})");
			}
		}

		public static void RecordFrameVisibilityChange(bool becameVisible, int frameNumber)
		{
			// Reset counters on new frame
			if (frameNumber != currentFrame)
			{
				// Check for mass blink on previous frame before resetting
				if (currentFrame >= 0 && (invisibleCountThisFrame >= MassBlinkThreshold || visibleCountThisFrame >= MassBlinkThreshold))
				{
					Log.Write("debug",
						$"[VOXEL-MASS-BLINK-FRAME] Frame {currentFrame}: " +
						$"{invisibleCountThisFrame} models went INVISIBLE, {visibleCountThisFrame} went VISIBLE");
				}

				currentFrame = frameNumber;
				invisibleCountThisFrame = 0;
				visibleCountThisFrame = 0;
			}

			if (becameVisible)
				visibleCountThisFrame++;
			else
				invisibleCountThisFrame++;
		}

		public static bool ShouldLogVisibilityChange(Actor self)
		{
			// Filter 1: Skip known toggle actors (miners, etc.)
			if (ExpectedToggleActors.Contains(self.Info.Name))
				return false;

			return true;
		}
	}

	public interface IRenderActorPreviewVoxelsInfo : ITraitInfoInterface
	{
		IEnumerable<ModelAnimation> RenderPreviewVoxels(IModelCache cache,
			ActorPreviewInitializer init, RenderVoxelsInfo rv, string image, Func<WRot> orientation, int facings, PaletteReference p);
	}

	public class RenderVoxelsInfo : TraitInfo, IRenderActorPreviewInfo, Requires<BodyOrientationInfo>
	{
		[Desc("Defaults to the actor name.")]
		public readonly string Image = null;

		[Desc("Custom palette name")]
		[PaletteReference]
		public readonly string Palette = null;

		[PaletteReference]
		[Desc("Custom PlayerColorPalette: BaseName")]
		public readonly string PlayerPalette = "player";

		[PaletteReference]
		public readonly string NormalsPalette = "normals";

		[PaletteReference]
		public readonly string ShadowPalette = "shadow";

		[Desc("Change the image size.")]
		public readonly float Scale = 12;

		public readonly WAngle LightPitch = WAngle.FromDegrees(50);
		public readonly WAngle LightYaw = WAngle.FromDegrees(240);
		public readonly float[] LightAmbientColor = [0.6f, 0.6f, 0.6f];
		public readonly float[] LightDiffuseColor = [0.4f, 0.4f, 0.4f];

		public override object Create(ActorInitializer init) { return new RenderVoxels(init.Self, this); }

		public virtual IEnumerable<IActorPreview> RenderPreview(ActorPreviewInitializer init)
		{
			var renderer = init.World.WorldActor.Trait<ModelRenderer>();
			var cache = init.World.WorldActor.Trait<IModelCache>();
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var faction = init.GetValue<FactionInit, string>(this);
			var ownerName = init.Get<OwnerInit>().InternalName;
			var sequences = init.World.Map.Sequences;
			var image = Image ?? init.Actor.Name;
			var facings = body.QuantizedFacings == -1 ?
				init.Actor.TraitInfo<IQuantizeBodyOrientationInfo>().QuantizedBodyFacings(init.Actor, sequences, faction) :
				body.QuantizedFacings;
			var palette = init.WorldRenderer.Palette(Palette ?? PlayerPalette + ownerName);

			var components = init.Actor.TraitInfos<IRenderActorPreviewVoxelsInfo>()
				.SelectMany(rvpi => rvpi.RenderPreviewVoxels(cache, init, this, image, init.GetOrientation(), facings, palette))
				.ToArray();

			yield return new ModelPreview(renderer, components, WVec.Zero, 0, Scale, LightPitch,
				LightYaw, LightAmbientColor, LightDiffuseColor, body.CameraPitch,
				palette, init.WorldRenderer.Palette(NormalsPalette), init.WorldRenderer.Palette(ShadowPalette));
		}
	}

	public class RenderVoxels : IRender, ITick, INotifyOwnerChanged
	{
		sealed class AnimationWrapper
		{
			readonly ModelAnimation model;
			readonly Actor self;
			readonly int componentIndex;
			bool cachedVisible;
			WVec cachedOffset;
			int lastVisibilityChangeTick = -1;
			bool lastVisibilityChangeWasVisible;

			public AnimationWrapper(ModelAnimation model, Actor self, int componentIndex)
			{
				this.model = model;
				this.self = self;
				this.componentIndex = componentIndex;
			}

			public bool Tick()
			{
				// Return to the caller whether the renderable position or size has changed
				var visible = model.IsVisible;
				var offset = model.OffsetFunc?.Invoke() ?? WVec.Zero;

				var updated = visible != cachedVisible || offset != cachedOffset;

				// Track visibility changes for mass blink detection and filtered logging
				if (visible != cachedVisible)
				{
					var worldTick = self.World.WorldTick;

					// Always record for mass blink detection
					VoxelBlinkDetector.RecordVisibilityChange(self, visible, worldTick);

					// Filter 2: Pattern-based filtering - skip paired toggles in same tick
					// (e.g., dock animations that go INVISIBLE then VISIBLE in same tick)
					var isPairedToggle = lastVisibilityChangeTick == worldTick && lastVisibilityChangeWasVisible != visible;

					// Only log if passes both filters
					if (!isPairedToggle && VoxelBlinkDetector.ShouldLogVisibilityChange(self))
					{
						// Check if DisableFunc exists and what it returns
						var disableFuncResult = model.DisableFunc?.Invoke() ?? false;
						var componentName = componentIndex == 0 ? "body" : $"part{componentIndex}";

						if (cachedVisible && !visible)
						{
							Log.Write("debug",
								$"[VOXEL-BLINK] Actor {self.ActorID} ({self.Info.Name}) {componentName} " +
								$"became INVISIBLE at tick {worldTick}, DisableFunc={disableFuncResult}");
						}
						else if (!cachedVisible && visible)
						{
							Log.Write("debug",
								$"[VOXEL-BLINK] Actor {self.ActorID} ({self.Info.Name}) {componentName} " +
								$"became VISIBLE at tick {worldTick}, DisableFunc={disableFuncResult}");
						}
					}

					lastVisibilityChangeTick = worldTick;
					lastVisibilityChangeWasVisible = visible;
				}

				cachedVisible = visible;
				cachedOffset = offset;

				return updated;
			}
		}

		public readonly RenderVoxelsInfo Info;
		public readonly ModelRenderer Renderer;

		readonly List<ModelAnimation> components = [];
		readonly Dictionary<ModelAnimation, AnimationWrapper> wrappers = [];

		readonly Actor self;
		readonly BodyOrientation body;
		readonly WRot camera;
		readonly WRot lightSource;

		public RenderVoxels(Actor self, RenderVoxelsInfo info)
		{
			this.self = self;
			Renderer = self.World.WorldActor.Trait<ModelRenderer>();
			Info = info;
			body = self.Trait<BodyOrientation>();
			camera = new WRot(WAngle.Zero, body.CameraPitch - new WAngle(256), new WAngle(256));
			lightSource = new WRot(WAngle.Zero, new WAngle(256) - info.LightPitch, info.LightYaw);
		}

		bool initializePalettes = true;
		public void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner) { initializePalettes = true; }

		void ITick.Tick(Actor self)
		{
			var updated = false;
			foreach (var w in wrappers.Values)
				updated |= w.Tick();

			if (updated)
				self.World.ScreenMap.AddOrUpdate(self);
		}

		protected PaletteReference colorPalette, normalsPalette, shadowPalette;
		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer wr)
		{
			if (initializePalettes)
			{
				var paletteName = Info.Palette ?? Info.PlayerPalette + self.Owner.InternalName;
				colorPalette = wr.Palette(paletteName);
				normalsPalette = wr.Palette(Info.NormalsPalette);
				shadowPalette = wr.Palette(Info.ShadowPalette);
				initializePalettes = false;
			}

			return
			[
				new ModelRenderable(
					Renderer, components, self.CenterPosition, 0, camera, Info.Scale,
					lightSource, Info.LightAmbientColor, Info.LightDiffuseColor,
					colorPalette, normalsPalette, shadowPalette)
			];
		}

		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer wr)
		{
			var pos = self.CenterPosition;
			foreach (var c in components)
				if (c.IsVisible)
					yield return c.ScreenBounds(pos, wr, Info.Scale);
		}

		public string Image => Info.Image ?? self.Info.Name;

		public void Add(ModelAnimation m)
		{
			var componentIndex = components.Count;
			components.Add(m);
			wrappers.Add(m, new AnimationWrapper(m, self, componentIndex));
		}

		public void Remove(ModelAnimation m)
		{
			components.Remove(m);
			wrappers.Remove(m);
		}
	}
}
