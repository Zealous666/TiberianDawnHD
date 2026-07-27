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
using System.Globalization;
using System.IO;
using OpenRA.FileFormats;
using OpenRA.Graphics;
using OpenRA.Mods.Cnc.FileFormats;
using OpenRA.Primitives;

namespace OpenRA.Mods.Cnc.UtilityCommands
{
	sealed class VxlToPngCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--vxl-to-png";

		bool IUtilityCommand.ValidateArguments(string[] args) => args.Length >= 4;

		[Desc("VXLFILE HVAFILE PALFILE [OPTIONS]",
			"Convert a VXL/HVA voxel model to PNG sprites, one per facing.",
			"Options:",
			"  --facings N         Number of rotation facings (default: 32)",
			"  --scale F           Screen pixels per world unit (default: 12)",
			"  --pitch F           Camera pitch degrees above horizontal (default: 30)",
			"  --yaw F             Camera yaw degrees; camera is located at this direction (default: 225 = SW)",
			"  --light-yaw F       Light source yaw degrees (default: 240)",
			"  --light-pitch F     Light source pitch degrees (default: 50)",
			"  --ambient F         Ambient light 0..1 (default: 0.6)",
			"  --diffuse F         Diffuse light 0..1 (default: 0.4)",
			"  --player-color X    Remap palette indices 16-31 to player color.",
			"                      X = 'gdi' (#C8B432), 'nod' (#B40000), or '#RRGGBB'.",
			"  --supersample N     Render at N× resolution, then box-filter downsample (default: 8)",
			"  --output-dir DIR    Output directory (default: current)",
			"  --remap-floor F     Exposure floor for remap normalization 0..1 (default: 0.0).",
			"                      Maps [floor,1.0] brightness onto the full ramp so the actual",
			"                      diffuse range drives the shading. Set to ambient-0.05 for best",
			"                      results (e.g. 0.55 when --ambient 0.6). Transferable: same",
			"                      formula applies to any future TS-Voxel import.",
			"  --saturation F      Body saturation 0..1 (default: 1.0 = full color). 0.1 = 90%",
			"                      desaturated (almost silver-gray). Never affects player-color region.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var vxlPath = args[1];
			var hvaPath = args[2];
			var palPath = args[3];

			var facings = GetArgInt(args, "--facings", 32);
			var scale = GetArgFloat(args, "--scale", 12f);
			var cameraPitch = GetArgFloat(args, "--pitch", 30f) * MathF.PI / 180f;
			var cameraYaw = GetArgFloat(args, "--yaw", 225f) * MathF.PI / 180f;
			var lightYaw = GetArgFloat(args, "--light-yaw", 240f) * MathF.PI / 180f;
			var lightPitch = GetArgFloat(args, "--light-pitch", 50f) * MathF.PI / 180f;
			var ambient = GetArgFloat(args, "--ambient", 0.6f);
			var diffuse = GetArgFloat(args, "--diffuse", 0.4f);
			var playerColorArg = GetArgString(args, "--player-color", null);
			var supersample = GetArgInt(args, "--supersample", 8);
			// Classic (TD/RA) non-linear facing→frame mapping for 32 facings. On by default for 32.
			var classicFacings = GetArgBool(args, "--classic-facings", facings == 32);
			// Tuning to align the rendered orientation with the in-game facing convention.
			var facingOffset = GetArgFloat(args, "--facing-offset", 0f) * MathF.PI / 180f;
			var facingFlip = GetArgBool(args, "--facing-flip", false);
			// Splat footprint = voxel spacing × this margin. 1.0 = tight/sharp, >1 = overlap/softer.
			var splatMargin = GetArgFloat(args, "--splat-margin", 1.0f);
			// Multiplies the body (non-player-color) brightness. 1.0 = unchanged, 0.9 = 10% darker.
			// The player-color region (palette indices 16-31) is never affected by this.
			var brightness = GetArgFloat(args, "--brightness", 1.0f);
			// Saturation of the body (non-player-color) pixels. 1.0 = full color, 0.0 = grayscale.
			// 0.1 = 90% desaturated (almost silver-gray). Never affects the player-color region.
			var saturation = GetArgFloat(args, "--saturation", 1.0f);
			// Two-layer player-color: emit an INDEXED overlay sheet (house-color indices 176-191)
			// for the remap region (TS palette indices 16-31), to be tinted by the in-game `player`
			// palette. The RGBA body keeps full lighting; the remap region is desaturated in the body.
			var remapSheet = GetArgString(args, "--remap-sheet", null);
			var twoLayer = remapSheet != null;
			// Exposure floor: maps [remapFloor, 1.0] → [0, 1] before assigning ramp indices.
			// Set to (ambient - 0.05) so the actual diffuse range drives the full ramp.
			// Pixels below the floor still get index 191 (darkest) rather than 0.
			var remapFloor = GetArgFloat(args, "--remap-floor", 0.0f);
			var outputDir = GetArgString(args, "--output-dir", Directory.GetCurrentDirectory());
			// Pivot offset: shift the model's center before rendering so the given world
			// position maps to the canvas center instead of the bounding-box center.
			// Positive X moves the content left (canvas center shifts right relative to model).
			var pivotOffsetX = GetArgFloat(args, "--pivot-offset-x", 0f);
			var pivotOffsetY = GetArgFloat(args, "--pivot-offset-y", 0f);
			var pivotOffsetZ = GetArgFloat(args, "--pivot-offset-z", 0f);

			var palette = ReadPalette(palPath);
			// In two-layer mode the player color comes from the indexed overlay, not a baked palette.
			if (playerColorArg != null && !twoLayer)
				ApplyPlayerColorRemap(palette, ParsePlayerColor(playerColorArg));

			// Render at supersample× resolution internally, then box-filter downsample
			var renderScale = scale * supersample;

			// Model-rotation angle (radians) for sprite frame i.
			// For classic 32-facing sprites the frame→facing mapping is NON-LINEAR
			// (see SpriteFacingsTable / OpenRA.Mods.Cnc.Util.SpriteFacings): the game picks
			// frame i for facings around SpriteFacingsTable[i], so we must render that angle.
			float FrameAngle(int i)
			{
				float baseWAngle;
				if (classicFacings && facings == 32)
					baseWAngle = SpriteFacingsTable[i];
				else
					baseWAngle = i * 1024f / facings;

				var rad = baseWAngle / 1024f * 2f * MathF.PI;
				if (facingFlip)
					rad = -rad;
				return rad + facingOffset;
			}

			var vxl = VxlReader.Load(vxlPath);
			var hva = HvaReader.Load(hvaPath);
			var tsNormals = TSNormals;

			// Light direction in world space
			var lx = MathF.Cos(lightYaw) * MathF.Cos(lightPitch);
			var ly = MathF.Sin(lightYaw) * MathF.Cos(lightPitch);
			var lz = MathF.Sin(lightPitch);

			// Camera orthonormal basis
			// Camera is located at (cos(yaw)*cos(pitch), sin(yaw)*cos(pitch), sin(pitch)) · distance
			// and looks toward the origin (the model center).
			//
			// Screen RIGHT: perpendicular to the camera in XY plane
			var crx = MathF.Sin(cameraYaw);
			var cry = -MathF.Cos(cameraYaw);
			// Screen UP
			var cux = -MathF.Cos(cameraYaw) * MathF.Sin(cameraPitch);
			var cuy = -MathF.Sin(cameraYaw) * MathF.Sin(cameraPitch);
			var cuz = MathF.Cos(cameraPitch);
			// Depth direction: TOWARD camera (camera position vector from origin).
			// Negate this for "depth from camera" where smaller = closer.
			// Camera position direction: (cos(yaw)*cos(pitch), sin(yaw)*cos(pitch), sin(pitch))
			var camDx = MathF.Cos(cameraYaw) * MathF.Cos(cameraPitch);
			var camDy = MathF.Sin(cameraYaw) * MathF.Cos(cameraPitch);
			var camDz = MathF.Sin(cameraPitch);
			// depth(voxel) = -dot(voxel, camPos) → smaller = closer to camera ← kept by z-buffer

			// Collect voxels: world position, world-space normal direction (pre-facing),
			// color, and per-voxel screen-space splat half-size.
			// This mirrors OpenRA's voxel shader (model.frag): each voxel surface point is
			// shaded by ITS OWN normal via ambient + diffuse·max(dot(N,L),0) — no cube faces.
			var voxels = new List<(float wx, float wy, float wz, float nx, float ny, float nz, byte color, float splatHalf)>();
			var maxSplatHalf = 0f;
			for (var limbIdx = 0; limbIdx < (int)vxl.LimbCount; limbIdx++)
			{
				var limb = vxl.Limbs[limbIdx];
				var t = BuildTransformMatrix(hva, limb, limbIdx, 0);
				var normals = limb.Type == NormalType.RedAlert2 ? RA2Normals : tsNormals;

				// World-space length of a unit grid step → voxel spacing → splat size
				var o = Util.MatrixVectorMultiply(t, new float[] { 0f, 0f, 0f, 1f });
				var sX = Util.MatrixVectorMultiply(t, new float[] { 1f, 0f, 0f, 1f });
				var sY = Util.MatrixVectorMultiply(t, new float[] { 0f, 1f, 0f, 1f });
				var sZ = Util.MatrixVectorMultiply(t, new float[] { 0f, 0f, 1f, 1f });
				static float Dist(float[] a, float[] b)
				{
					var dx = a[0] - b[0]; var dy = a[1] - b[1]; var dz = a[2] - b[2];
					return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
				}

				var spacing = MathF.Max(Dist(sX, o), MathF.Max(Dist(sY, o), Dist(sZ, o)));
				var splatHalf = spacing * renderScale * 0.5f * splatMargin;
				if (splatHalf > maxSplatHalf) maxSplatHalf = splatHalf;

				for (var gx = 0; gx < limb.Size[0]; gx++)
				{
					for (var gy = 0; gy < limb.Size[1]; gy++)
					{
						var col = limb.VoxelMap[gx, gy];
						if (col == null) continue;
						foreach (var kv in col)
						{
							var elem = kv.Value;
							var local = new float[] { gx + 0.5f, gy + 0.5f, kv.Key + 0.5f, 1f };
							var world = Util.MatrixVectorMultiply(t, local);

							// Resolve raw normal, then transform into world space using the
							// linear part of t (w=0 ignores translation), then renormalise.
							float lnx, lny, lnz;
							var nidx = elem.Normal * 3;
							if (nidx + 2 < normals.Length)
							{
								lnx = normals[nidx]; lny = normals[nidx + 1]; lnz = normals[nidx + 2];
							}
							else { lnx = 0f; lny = 0f; lnz = 1f; }

							var nWorld = Util.MatrixVectorMultiply(t, new float[] { lnx, lny, lnz, 0f });
							var nlen = MathF.Sqrt(nWorld[0] * nWorld[0] + nWorld[1] * nWorld[1] + nWorld[2] * nWorld[2]);
							if (nlen < 0.0001f) { nWorld[0] = 0f; nWorld[1] = 0f; nWorld[2] = 1f; nlen = 1f; }

							voxels.Add((world[0], world[1], world[2],
								nWorld[0] / nlen, nWorld[1] / nlen, nWorld[2] / nlen,
								elem.Color, splatHalf));
						}
					}
				}
			}

			if (voxels.Count == 0)
			{
				Console.WriteLine("No voxels found — check that the VXL file is not empty.");
				return;
			}

			// Center model on rotation axis
			var wxMin = float.MaxValue; var wxMax = float.MinValue;
			var wyMin = float.MaxValue; var wyMax = float.MinValue;
			var wzMin = float.MaxValue; var wzMax = float.MinValue;
			foreach (var (wx, wy, wz, _, _, _, _, _) in voxels)
			{
				if (wx < wxMin) wxMin = wx; if (wx > wxMax) wxMax = wx;
				if (wy < wyMin) wyMin = wy; if (wy > wyMax) wyMax = wy;
				if (wz < wzMin) wzMin = wz; if (wz > wzMax) wzMax = wz;
			}
			var worldCx = (wxMin + wxMax) / 2f;
			var worldCy = (wyMin + wyMax) / 2f;
			var worldCz = (wzMin + wzMax) / 2f;
			for (var i = 0; i < voxels.Count; i++)
			{
				var (wx, wy, wz, nx, ny, nz, c, sh) = voxels[i];
				voxels[i] = (wx - worldCx + pivotOffsetX, wy - worldCy + pivotOffsetY, wz - worldCz + pivotOffsetZ, nx, ny, nz, c, sh);
			}

			// Determine canvas size from max screen bounding box across all facings,
			// padded by the splat half-size. Drawing always places content at a fixed
			// canvasSize/2 center (see canvCx/canvCy below), so the canvas must be sized
			// from the max ABSOLUTE screen coordinate in each axis (symmetric radius around
			// the origin/pivot) — not just the raw extent (max-min). Otherwise an
			// off-center pivot (e.g. --pivot-offset-*) yields an asymmetric bounding box
			// that fits in size but clips on one side once centered at a fixed point.
			var maxAbsX = 0f;
			var maxAbsY = 0f;
			for (var facing = 0; facing < facings; facing++)
			{
				var angle = FrameAngle(facing);
				var cosA = MathF.Cos(angle);
				var sinA = MathF.Sin(angle);
				var minSx = float.MaxValue; var maxSx = float.MinValue;
				var minSy = float.MaxValue; var maxSy = float.MinValue;
				foreach (var (wx, wy, wz, _, _, _, _, _) in voxels)
				{
					var rx = wx * cosA - wy * sinA;
					var ry = wx * sinA + wy * cosA;
					var sx = (rx * crx + ry * cry) * renderScale;
					var sy = -(rx * cux + ry * cuy + wz * cuz) * renderScale;
					if (sx < minSx) minSx = sx; if (sx > maxSx) maxSx = sx;
					if (sy < minSy) minSy = sy; if (sy > maxSy) maxSy = sy;
				}

				var axAbs = MathF.Max(MathF.Abs(minSx), MathF.Abs(maxSx)) + maxSplatHalf + 1f;
				var ayAbs = MathF.Max(MathF.Abs(minSy), MathF.Abs(maxSy)) + maxSplatHalf + 1f;
				if (axAbs > maxAbsX) maxAbsX = axAbs;
				if (ayAbs > maxAbsY) maxAbsY = ayAbs;
			}

			var padding = 6;
			var canvasW = (int)MathF.Ceiling(maxAbsX * 2f) + padding * 2;
			var canvasH = (int)MathF.Ceiling(maxAbsY * 2f) + padding * 2;
			var canvasSize = Math.Max(canvasW, canvasH);
			if (canvasSize % 2 != 0) canvasSize++;

			Directory.CreateDirectory(outputDir);
			var prefix = Path.GetFileNameWithoutExtension(vxlPath).ToLowerInvariant();
			var outCanvasSize = supersample > 1 ? canvasSize / supersample : canvasSize;
			Console.WriteLine($"Voxels: {voxels.Count} | Render: {canvasSize}x{canvasSize} | Output: {outCanvasSize}x{outCanvasSize} (SS={supersample}×) | Facings: {facings}");

			// Splat rasterizer: fill a screen-aligned square footprint per voxel, z-buffered.
			// remapGray/remapCov (optional): per-pixel grayscale + coverage of the winning voxel,
			// but only when it is a remap voxel — used to build the player-color overlay layer.
			static void FillSquare(byte[] rgba, float[] zbuf, int canvSize,
				float cxp, float cyp, float half, float depth, byte r, byte g, byte b, byte bodyAlpha,
				byte[] remapGray, byte[] remapCov, byte remapValue, bool isRemap)
			{
				var x0 = (int)MathF.Floor(cxp - half);
				var x1 = (int)MathF.Ceiling(cxp + half);
				var y0 = (int)MathF.Floor(cyp - half);
				var y1 = (int)MathF.Ceiling(cyp + half);
				for (var py = y0; py <= y1; py++)
				{
					if (py < 0 || py >= canvSize) continue;
					for (var px = x0; px <= x1; px++)
					{
						if (px < 0 || px >= canvSize) continue;
						var idx = py * canvSize + px;
						if (depth >= zbuf[idx]) continue;
						zbuf[idx] = depth;
						rgba[idx * 4 + 0] = r;
						rgba[idx * 4 + 1] = g;
						rgba[idx * 4 + 2] = b;
						// Remap voxels are transparent in the body (the overlay layer fills them),
						// but they still occupy the z-buffer so occlusion stays correct.
						rgba[idx * 4 + 3] = bodyAlpha;

						// The winning voxel determines overlay coverage (handles occlusion):
						// a non-remap voxel in front clears any remap behind it.
						if (remapGray != null)
						{
							remapGray[idx] = isRemap ? remapValue : (byte)0;
							remapCov[idx] = isRemap ? (byte)255 : (byte)0;
						}
					}
				}
			}

			var remapFrames = twoLayer ? new List<byte[]>() : null;
			var finalSize = 0;

			for (var facing = 0; facing < facings; facing++)
			{
				var angle = FrameAngle(facing);
				var cosA = MathF.Cos(angle);
				var sinA = MathF.Sin(angle);

				var rgba = new byte[canvasSize * canvasSize * 4];
				var zbuf = new float[canvasSize * canvasSize];
				Array.Fill(zbuf, float.MaxValue);

				var remapGray = twoLayer ? new byte[canvasSize * canvasSize] : null;
				var remapCov = twoLayer ? new byte[canvasSize * canvasSize] : null;

				var canvCx = canvasSize / 2;
				var canvCy = canvasSize / 2;

				foreach (var (wx, wy, wz, nx0, ny0, nz0, colorIdx, splatHalf) in voxels)
				{
					if ((palette[colorIdx] >> 24) == 0) continue;

					// Rotate world position by facing angle (around Z)
					var rx = wx * cosA - wy * sinA;
					var ry = wx * sinA + wy * cosA;
					var rz = wz;

					// Screen-space center of this voxel
					var sx = (rx * crx + ry * cry) * renderScale;
					var sy = -(rx * cux + ry * cuy + rz * cuz) * renderScale;

					// Depth: smaller = closer to camera
					var depth = -(rx * camDx + ry * camDy + rz * camDz);

					// Rotate the voxel's world-space normal by facing (around Z), then shade
					// exactly as model.frag: intensity = ambient + diffuse·max(dot(N,L),0)
					var nx = nx0 * cosA - ny0 * sinA;
					var ny = nx0 * sinA + ny0 * cosA;
					var nz = nz0;

					var ndotl = MathF.Max(0f, nx * lx + ny * ly + nz * lz);
					var bright = ambient + diffuse * ndotl;

					// Player-color region (indices 16-31) keeps full brightness; the rest of the
					// body is scaled by --brightness (e.g. 0.9 = 10% darker).
					var isRemapColor = colorIdx >= 16 && colorIdx <= 31;
					var bodyBright = bright * (isRemapColor ? 1f : brightness);

					var baseColor = palette[colorIdx];
					var r = (byte)MathF.Min(255f, ((baseColor >> 16) & 0xFF) * bodyBright);
					var g = (byte)MathF.Min(255f, ((baseColor >> 8) & 0xFF) * bodyBright);
					var b = (byte)MathF.Min(255f, (baseColor & 0xFF) * bodyBright);

					// Desaturate body (non-player-color) pixels. lum mix: sat=1 = full color, sat=0 = grayscale.
					if (!isRemapColor && saturation < 1f)
					{
						var lum = 0.299f * r + 0.587f * g + 0.114f * b;
						r = (byte)MathF.Round(lum + saturation * (r - lum));
						g = (byte)MathF.Round(lum + saturation * (g - lum));
						b = (byte)MathF.Round(lum + saturation * (b - lum));
					}

					// Remap voxels (TS palette indices 16-31) carry the player color in two-layer
					// mode: render them transparent in the body (overlay fills them) and record
					// their lit brightness for the overlay layer.
					var isRemap = twoLayer && isRemapColor;
					byte remapValue = 0;
					byte bodyAlpha = 255;
					if (isRemap)
					{
						// Use raw 'bright' instead of tinted max(r,g,b) so remapFloor
						// normalization works independently of the palette's player color.
						remapValue = (byte)MathF.Min(255f, bright * 255f);
						bodyAlpha = 0;
					}

					FillSquare(rgba, zbuf, canvasSize,
						sx + canvCx, sy + canvCy, splatHalf, depth, r, g, b, bodyAlpha,
						remapGray, remapCov, remapValue, isRemap);
				}

				byte[] outRgba;
				int outSize;
				if (supersample > 1)
				{
					outSize = canvasSize / supersample;
					outRgba = BoxDownsample(rgba, canvasSize, outSize, supersample);
				}
				else
				{
					outSize = canvasSize;
					outRgba = rgba;
				}

				finalSize = outSize;

				var png = new Png(outRgba, SpriteFrameType.Rgba32, outSize, outSize);
				png.Save(Path.Combine(outputDir, $"{prefix}-{facing:D4}.png"));

				if (twoLayer)
					remapFrames.Add(DownsampleRemapToIndices(remapGray, remapCov, canvasSize, outSize, Math.Max(1, supersample), remapFloor));
			}

			Console.WriteLine($"Saved {prefix}-[0000..{facings - 1:D4}].png to {outputDir}");

			if (twoLayer)
			{
				WriteRemapSheet(remapSheet, remapFrames, finalSize);
				Console.WriteLine($"Saved player-color overlay sheet ({remapFrames.Count} frames) to {remapSheet}");
			}
		}

		// Player-color house ramp indices in the CnC `player` palette (bright→dark).
		const int RemapRampStart = 176;
		const int RemapRampCount = 16;

		// Downsample the remap gray+coverage buffers and map each covered output pixel onto the
		// house-color ramp (176-191) by brightness. Uncovered pixels become index 0 (transparent).
		// Exposure floor normalization (transferable algorithm):
		//   Maps [remapFloor, 1.0] onto the full ramp so that diffuse shading
		//   drives the entire shade range. Formula: floor = ambient - 0.05.
		//   e.g. ambient=0.6, diffuse=0.4 → floor=0.55
		//     • top face (bright=1.0)  → normalized=1.0 → index 176 (brightest)
		//     • side (bright=0.8)      → normalized=0.56 → index ~183 (medium)
		//     • underside (bright=0.6) → normalized=0.11 → index ~189 (very dark)
		static byte[] DownsampleRemapToIndices(byte[] gray, byte[] cov, int srcSize, int dstSize, int ss, float remapFloor = 0f)
		{
			var dst = new byte[dstSize * dstSize];
			var ssq = ss * ss;
			var floorD = (double)Math.Clamp(remapFloor, 0f, 0.99f);
			var rangeD = 1.0 - floorD;
			for (var dy = 0; dy < dstSize; dy++)
			{
				for (var dx = 0; dx < dstSize; dx++)
				{
					double sumGray = 0, sumCov = 0;
					for (var sy = 0; sy < ss; sy++)
					{
						for (var sx = 0; sx < ss; sx++)
						{
							var si = (dy * ss + sy) * srcSize + (dx * ss + sx);
							var c = cov[si] / 255.0;
							sumGray += gray[si] * c;
							sumCov += c;
						}
					}

					var di = dy * dstSize + dx;
					// Require majority coverage so the overlay edge tracks the remap region.
					if (sumCov / ssq >= 0.5 && sumCov > 0.0001)
					{
						var grayNorm = sumGray / sumCov / 255.0;
						// Normalize to [floor, 1.0] → [0, 1]: clamp below-floor pixels to darkest shade.
						var normalized = Math.Clamp((grayNorm - floorD) / rangeD, 0.0, 1.0);
						var step = (int)Math.Round((1.0 - normalized) * (RemapRampCount - 1));
						dst[di] = (byte)(RemapRampStart + step);
					}
					else
						dst[di] = 0;
				}
			}

			return dst;
		}

		// Assemble per-facing index frames into one indexed PNG sheet (grid) that PngSheet can
		// auto-slice via FrameSize/FrameAmount. The embedded palette is only for file validity —
		// at render time the sequence's `player` palette provides the actual colors.
		static void WriteRemapSheet(string path, List<byte[]> frames, int frameSize)
		{
			var cols = 8;
			var rows = (frames.Count + cols - 1) / cols;
			var sheetW = cols * frameSize;
			var sheetH = rows * frameSize;
			var sheet = new byte[sheetW * sheetH];

			for (var f = 0; f < frames.Count; f++)
			{
				var ox = f % cols * frameSize;
				var oy = f / cols * frameSize;
				var frame = frames[f];
				for (var y = 0; y < frameSize; y++)
					for (var x = 0; x < frameSize; x++)
						sheet[(oy + y) * sheetW + (ox + x)] = frame[y * frameSize + x];
			}

			// Dummy palette: index 0 transparent, 176-191 a grayscale ramp (bright→dark).
			var palette = new Color[256];
			palette[0] = Color.FromArgb(0, 0, 0, 0);
			for (var i = 0; i < RemapRampCount; i++)
			{
				var v = (byte)(255 - i * (255 / (RemapRampCount - 1)));
				palette[RemapRampStart + i] = Color.FromArgb(255, v, v, v);
			}

			var embedded = new Dictionary<string, string>
			{
				["FrameSize"] = $"{frameSize},{frameSize}",
				["FrameAmount"] = frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
			};

			var png = new Png(sheet, SpriteFrameType.Indexed8, sheetW, sheetH, palette, embedded);
			png.Save(path);
		}

		// Box-filter downsample: average ss×ss input pixels per output pixel (alpha-aware)
		static byte[] BoxDownsample(byte[] src, int srcSize, int dstSize, int ss)
		{
			var dst = new byte[dstSize * dstSize * 4];
			var ssq = ss * ss;
			for (var dy = 0; dy < dstSize; dy++)
			{
				for (var dx = 0; dx < dstSize; dx++)
				{
					double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
					for (var sy2 = 0; sy2 < ss; sy2++)
					{
						for (var sx2 = 0; sx2 < ss; sx2++)
						{
							var si = ((dy * ss + sy2) * srcSize + (dx * ss + sx2)) * 4;
							var a = src[si + 3] / 255.0;
							sumR += src[si + 0] * a;
							sumG += src[si + 1] * a;
							sumB += src[si + 2] * a;
							sumA += a;
						}
					}

					var di = (dy * dstSize + dx) * 4;
					if (sumA > 0.0001)
					{
						dst[di + 0] = (byte)Math.Min(255, sumR / sumA + 0.5);
						dst[di + 1] = (byte)Math.Min(255, sumG / sumA + 0.5);
						dst[di + 2] = (byte)Math.Min(255, sumB / sumA + 0.5);
						dst[di + 3] = (byte)Math.Min(255, sumA / ssq * 255.0 + 0.5);
					}
				}
			}

			return dst;
		}

		// Replicate Voxel.cs TransformationMatrix(limb, frame=0) logic
		static float[] BuildTransformMatrix(HvaReader hva, VxlLimb limb, int limbIdx, int frame)
		{
			var t = new float[16];
			Array.Copy(hva.Transforms, 16 * ((int)hva.LimbCount * frame + limbIdx), t, 0, 16);
			t[12] *= limb.Scale * (limb.Bounds[3] - limb.Bounds[0]) / limb.Size[0];
			t[13] *= limb.Scale * (limb.Bounds[4] - limb.Bounds[1]) / limb.Size[1];
			t[14] *= limb.Scale * (limb.Bounds[5] - limb.Bounds[2]) / limb.Size[2];
			t = Util.MatrixMultiply(t, Util.TranslationMatrix(limb.Bounds[0], limb.Bounds[1], limb.Bounds[2]));
			t = Util.MatrixMultiply(Util.ScaleMatrix(limb.Scale, -limb.Scale, limb.Scale), t);
			return t;
		}

		// Read 6-bit RGB palette (768 bytes = 256 × {R, G, B} in 0..63 range)
		static uint[] ReadPalette(string path)
		{
			var pal = new uint[256];
			using var f = File.OpenRead(path);
			for (var i = 0; i < 256; i++)
			{
				var r = (byte)f.ReadByte();
				var g = (byte)f.ReadByte();
				var b = (byte)f.ReadByte();
				r = (byte)((r << 2) | (r >> 4));
				g = (byte)((g << 2) | (g >> 4));
				b = (byte)((b << 2) | (b >> 4));
				pal[i] = (uint)((255 << 24) | (r << 16) | (g << 8) | b);
			}

			pal[0] = 0;
			return pal;
		}

		static int GetArgInt(string[] args, string flag, int def)
		{
			var i = Array.IndexOf(args, flag);
			if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v)) return v;
			return def;
		}

		static float GetArgFloat(string[] args, string flag, float def)
		{
			var i = Array.IndexOf(args, flag);
			if (i >= 0 && i + 1 < args.Length &&
				float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
			return def;
		}

		static string GetArgString(string[] args, string flag, string def)
		{
			var i = Array.IndexOf(args, flag);
			return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
		}

		// Bool flag: "--flag" alone or "--flag true/false".
		static bool GetArgBool(string[] args, string flag, bool def)
		{
			var i = Array.IndexOf(args, flag);
			if (i < 0)
				return def;
			if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var v))
				return v;
			return true;
		}

		// Classic TD/RA 32-facing sprite frame → facing (WAngle 0..1023) mapping.
		// Mirrors OpenRA.Mods.Cnc.Util.SpriteFacings. Frame i represents this facing.
		static readonly int[] SpriteFacingsTable =
		[
			0, 40, 74, 112, 146, 172, 200, 228,
			256, 284, 312, 340, 370, 402, 436, 472,
			512, 552, 588, 626, 658, 684, 712, 740,
			768, 796, 824, 852, 882, 914, 948, 984
		];

		static (float H, float S, float V) RgbToHsv(float r, float g, float b)
		{
			var max = MathF.Max(r, MathF.Max(g, b));
			var min = MathF.Min(r, MathF.Min(g, b));
			var delta = max - min;
			var v = max;
			var s = max < 0.0001f ? 0f : delta / max;
			float h = 0f;
			if (delta > 0.0001f)
			{
				if (max == r)      h = (g - b) / delta % 6f;
				else if (max == g) h = (b - r) / delta + 2f;
				else               h = (r - g) / delta + 4f;
				h /= 6f;
				if (h < 0) h += 1f;
			}
			return (h, s, v);
		}

		static (float R, float G, float B) HsvToRgb(float h, float s, float v)
		{
			if (s < 0.0001f) return (v, v, v);
			h *= 6f;
			var i = (int)h;
			var f = h - i;
			var p = v * (1f - s);
			var q = v * (1f - s * f);
			var t = v * (1f - s * (1f - f));
			return (i % 6) switch
			{
				0 => (v, t, p),
				1 => (q, v, p),
				2 => (p, v, t),
				3 => (p, q, v),
				4 => (t, p, v),
				_ => (v, p, q),
			};
		}

		static (float R, float G, float B) ParsePlayerColor(string arg)
		{
			var hex = arg.ToLowerInvariant() switch
			{
				"gdi" => "C8B432",
				"nod" => "B40000",
				_ => arg.TrimStart('#'),
			};
			var rgb = Convert.ToUInt32(hex, 16);
			return ((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f);
		}

		// Replicate PlayerColorRemap.cs: keep brightness (V) of original color, replace H+S with player color
		static void ApplyPlayerColorRemap(uint[] palette, (float R, float G, float B) playerColor)
		{
			var (ph, ps, pv) = RgbToHsv(playerColor.R, playerColor.G, playerColor.B);
			for (var i = 16; i <= 31; i++)
			{
				var entry = palette[i];
				var r = ((entry >> 16) & 0xFF) / 255f;
				var g = ((entry >> 8) & 0xFF) / 255f;
				var b = (entry & 0xFF) / 255f;
				var (_, _, ov) = RgbToHsv(r, g, b);
				var (nr, ng, nb) = HsvToRgb(ph, ps, ov * pv);
				var nr8 = (byte)(nr * 255f + 0.5f);
				var ng8 = (byte)(ng * 255f + 0.5f);
				var nb8 = (byte)(nb * 255f + 0.5f);
				palette[i] = (0xFF000000u) | ((uint)nr8 << 16) | ((uint)ng8 << 8) | nb8;
			}
		}

		// Normal vector table for TiberianSun voxels
		// Source: https://web.archive.org/web/20041022134721/https://www.sleipnirstuff.com/forum/viewtopic.php?t=8048
		static readonly float[] TSNormals =
		[
			 0.671214f,  0.198492f, -0.714194f,
			 0.269643f,  0.584394f, -0.765360f,
			-0.040546f,  0.096988f, -0.994459f,
			-0.572428f, -0.091914f, -0.814787f,
			-0.171401f, -0.572710f, -0.801639f,
			 0.362557f, -0.302999f, -0.881331f,
			 0.810347f, -0.348972f, -0.470698f,
			 0.103962f,  0.938672f, -0.328767f,
			-0.324047f,  0.587669f, -0.741376f,
			-0.800865f,  0.340461f, -0.492647f,
			-0.665498f, -0.590147f, -0.456989f,
			 0.314767f, -0.803002f, -0.506073f,
			 0.972629f,  0.151076f, -0.176550f,
			 0.680291f,  0.684236f, -0.262727f,
			-0.520079f,  0.827777f, -0.210483f,
			-0.961644f, -0.179001f, -0.207847f,
			-0.262714f, -0.937451f, -0.228401f,
			 0.219707f, -0.971301f,  0.091125f,
			 0.923808f, -0.229975f,  0.306087f,
			-0.082489f,  0.970660f,  0.225866f,
			-0.591798f,  0.696790f,  0.405289f,
			-0.925296f,  0.366601f,  0.097111f,
			-0.705051f, -0.687775f,  0.172828f,
			 0.732400f, -0.680367f, -0.026305f,
			 0.855162f,  0.374582f,  0.358311f,
			 0.473006f,  0.836480f,  0.276705f,
			-0.097617f,  0.654112f,  0.750072f,
			-0.904124f, -0.153725f,  0.398658f,
			-0.211916f, -0.858090f,  0.467732f,
			 0.500227f, -0.674408f,  0.543091f,
			 0.584539f, -0.110249f,  0.803841f,
			 0.437373f,  0.454644f,  0.775889f,
			-0.042441f,  0.083318f,  0.995619f,
			-0.596251f,  0.220132f,  0.772028f,
			-0.506455f, -0.396977f,  0.765449f,
			 0.070569f, -0.478474f,  0.875262f,
		];

		// RA2 normal table (244 normals), source as above.
		static readonly float[] RA2Normals =
		[
			0.526578f, -0.359621f, -0.770317f, 0.150482f, 0.435984f, 0.887284f,
			0.414195f, 0.738255f, -0.532374f, 0.075152f, 0.916249f, -0.393498f,
			-0.316149f, 0.930736f, -0.183793f, -0.773819f, 0.623334f, -0.112510f,
			-0.900842f, 0.428537f, -0.069568f, -0.998942f, -0.010971f, 0.044665f,
			-0.979761f, -0.157670f, -0.123324f, -0.911274f, -0.362371f, -0.195620f,
			-0.624069f, -0.720941f, -0.301301f, -0.310173f, -0.809345f, -0.498752f,
			0.146613f, -0.815819f, -0.559414f, -0.716516f, -0.694356f, -0.066888f,
			0.503972f, -0.114202f, -0.856137f, 0.455491f, 0.872627f, -0.176211f,
			-0.005010f, -0.114373f, -0.993425f, -0.104675f, -0.327701f, -0.938965f,
			0.560412f, 0.752589f, -0.345756f, -0.060576f, 0.821628f, -0.566796f,
			-0.302341f, 0.797007f, -0.522847f, -0.671543f, 0.670740f, -0.314863f,
			-0.778401f, -0.128357f, 0.614505f, -0.924050f, 0.278382f, -0.261985f,
			-0.699773f, -0.550491f, -0.455278f, -0.568248f, -0.517189f, -0.640008f,
			0.054098f, -0.932864f, -0.356143f, 0.758382f, 0.572893f, -0.310888f,
			0.003620f, 0.305026f, -0.952337f, -0.060850f, -0.986886f, -0.149511f,
			0.635230f, 0.045478f, -0.770983f, 0.521705f, 0.241309f, -0.818287f,
			0.269404f, 0.635425f, -0.723641f, 0.045676f, 0.672754f, -0.738455f,
			-0.180511f, 0.674657f, -0.715719f, -0.397131f, 0.636640f, -0.661042f,
			-0.552004f, 0.472515f, -0.687038f, -0.772170f, 0.083090f, -0.629960f,
			-0.669819f, -0.119533f, -0.732840f, -0.540455f, -0.318444f, -0.778782f,
			-0.386135f, -0.522789f, -0.759994f, -0.261466f, -0.688567f, -0.676395f,
			-0.019412f, -0.696103f, -0.717680f, 0.303569f, -0.481844f, -0.821993f,
			0.681939f, -0.195129f, -0.704900f, -0.244889f, -0.116562f, -0.962519f,
			0.800759f, -0.022979f, -0.598546f, -0.370275f, 0.095584f, -0.923991f,
			-0.330671f, -0.326578f, -0.885440f, -0.163220f, -0.527579f, -0.833679f,
			0.126390f, -0.313146f, -0.941257f, 0.349548f, -0.272226f, -0.896498f,
			0.239918f, -0.085825f, -0.966992f, 0.390845f, 0.081537f, -0.916838f,
			0.255267f, 0.268697f, -0.928785f, 0.146245f, 0.480438f, -0.864749f,
			-0.326016f, 0.478456f, -0.815349f, -0.469682f, -0.112519f, -0.875636f,
			0.818440f, -0.258520f, -0.513151f, -0.474318f, 0.292238f, -0.830433f,
			0.778943f, 0.395842f, -0.486371f, 0.624094f, 0.393773f, -0.674870f,
			0.740886f, 0.203834f, -0.639953f, 0.480217f, 0.565768f, -0.670297f,
			0.380930f, 0.424535f, -0.821378f, -0.093422f, 0.501124f, -0.860318f,
			-0.236485f, 0.296198f, -0.925387f, -0.131531f, 0.093959f, -0.986849f,
			-0.823562f, 0.295777f, -0.484006f, 0.611066f, -0.624304f, -0.486664f,
			0.069496f, -0.520330f, -0.851133f, 0.226522f, -0.664879f, -0.711775f,
			0.471308f, -0.568904f, -0.673957f, 0.388425f, -0.742624f, -0.545560f,
			0.783675f, -0.480729f, -0.393385f, 0.962394f, 0.135676f, -0.235349f,
			0.876607f, 0.172034f, -0.449406f, 0.633405f, 0.589793f, -0.500941f,
			0.182276f, 0.800658f, -0.570721f, 0.177003f, 0.764134f, 0.620297f,
			-0.544016f, 0.675515f, -0.497721f, -0.679297f, 0.286467f, -0.675642f,
			-0.590391f, 0.091369f, -0.801929f, -0.824360f, -0.133124f, -0.550189f,
			-0.715794f, -0.334542f, -0.612961f, 0.174286f, -0.892484f, 0.416049f,
			-0.082528f, -0.837123f, -0.540753f, 0.283331f, -0.880874f, -0.379189f,
			0.675134f, -0.426627f, -0.601817f, 0.843720f, -0.512335f, -0.160156f,
			0.977304f, -0.098556f, -0.187520f, 0.846295f, 0.522672f, -0.102947f,
			0.677141f, 0.721325f, -0.145501f, 0.320965f, 0.870892f, -0.372194f,
			-0.178978f, 0.911533f, -0.370236f, -0.447169f, 0.826701f, -0.341474f,
			-0.703203f, 0.496328f, -0.509081f, -0.977181f, 0.063563f, -0.202674f,
			-0.878170f, -0.412938f, 0.241455f, -0.835831f, -0.358550f, -0.415728f,
			-0.499174f, -0.693433f, -0.519592f, -0.188789f, -0.923753f, -0.333225f,
			0.192254f, -0.969361f, -0.152896f, 0.515940f, -0.783907f, -0.345392f,
			0.905925f, -0.300952f, -0.297871f, 0.991112f, -0.127746f, 0.037107f,
			0.995135f, 0.098424f, -0.004383f, 0.760123f, 0.646277f, 0.067367f,
			0.205221f, 0.959580f, -0.192591f, -0.042750f, 0.979513f, -0.196791f,
			-0.438017f, 0.898927f, 0.008492f, -0.821994f, 0.480785f, -0.305239f,
			-0.899917f, 0.081710f, -0.428337f, -0.926612f, -0.144618f, -0.347096f,
			-0.793660f, -0.557792f, -0.242839f, -0.431350f, -0.847779f, -0.308558f,
			-0.005492f, -0.965000f, 0.262193f, 0.587905f, -0.804026f, -0.088940f,
			0.699493f, -0.667686f, -0.254765f, 0.889303f, 0.359795f, -0.282291f,
			0.780972f, 0.197037f, 0.592672f, 0.520121f, 0.506696f, 0.687557f,
			0.403895f, 0.693961f, 0.596060f, -0.154983f, 0.899236f, 0.409090f,
			-0.657338f, 0.537168f, 0.528543f, -0.746195f, 0.334091f, 0.575827f,
			-0.624952f, -0.049144f, 0.779115f, 0.318141f, -0.254715f, 0.913185f,
			-0.555897f, 0.405294f, 0.725752f, -0.794434f, 0.099406f, 0.599160f,
			-0.640361f, -0.689463f, 0.338495f, -0.126713f, -0.734095f, 0.667120f,
			0.105457f, -0.780817f, 0.615795f, 0.407993f, -0.480916f, 0.776055f,
			0.695136f, -0.545120f, 0.468647f, 0.973191f, -0.006489f, 0.229908f,
			0.946894f, 0.317509f, -0.050799f, 0.563583f, 0.825612f, 0.027183f,
			0.325773f, 0.945423f, 0.006949f, -0.171821f, 0.985097f, -0.007815f,
			-0.670441f, 0.739939f, 0.054769f, -0.822981f, 0.554962f, 0.121322f,
			-0.966193f, 0.117857f, 0.229307f, -0.953769f, -0.294704f, 0.058945f,
			-0.864387f, -0.502728f, -0.010015f, -0.530609f, -0.842006f, -0.097366f,
			-0.162618f, -0.984075f, 0.071772f, 0.081447f, -0.996011f, 0.036439f,
			0.745984f, -0.665963f, 0.000762f, 0.942057f, -0.329269f, -0.064106f,
			0.939702f, -0.281090f, 0.194803f, 0.771214f, 0.550670f, 0.319363f,
			0.641348f, 0.730690f, 0.234021f, 0.080682f, 0.996691f, 0.009879f,
			-0.046725f, 0.976643f, 0.209725f, -0.531076f, 0.821001f, 0.209562f,
			-0.695815f, 0.655990f, 0.292435f, -0.976122f, 0.216709f, -0.014913f,
			-0.961661f, -0.144129f, 0.233314f, -0.772084f, -0.613647f, 0.165299f,
			-0.449600f, -0.836060f, 0.314426f, -0.392700f, -0.914616f, 0.096247f,
			0.390589f, -0.919470f, 0.044890f, 0.582529f, -0.799198f, 0.148127f,
			0.866431f, -0.489812f, 0.096864f, 0.904587f, 0.111498f, 0.411450f,
			0.953537f, 0.232330f, 0.191806f, 0.497311f, 0.770803f, 0.398177f,
			0.194066f, 0.956320f, 0.218611f, 0.422876f, 0.882276f, 0.206797f,
			-0.373797f, 0.849566f, 0.372174f, -0.534497f, 0.714023f, 0.452200f,
			-0.881827f, 0.237160f, 0.407598f, -0.904948f, -0.014069f, 0.425289f,
			-0.751827f, -0.512817f, 0.414458f, -0.501015f, -0.697917f, 0.511758f,
			-0.235190f, -0.925923f, 0.295555f, 0.228983f, -0.953940f, 0.193819f,
			0.734025f, -0.634898f, 0.241062f, 0.913753f, -0.063253f, -0.401316f,
			0.905735f, -0.161487f, 0.391875f, 0.858930f, 0.342446f, 0.380749f,
			0.624486f, 0.607581f, 0.490777f, 0.289264f, 0.857479f, 0.425508f,
			0.069968f, 0.902169f, 0.425671f, -0.286180f, 0.940700f, 0.182165f,
			-0.574013f, 0.805119f, -0.149309f, 0.111258f, 0.099718f, -0.988776f,
			-0.305393f, -0.944228f, -0.123160f, -0.601166f, -0.789576f, 0.123163f,
			-0.290645f, -0.812140f, 0.505919f, -0.064920f, -0.877163f, 0.475785f,
			0.408301f, -0.862216f, 0.299789f, 0.566097f, -0.725566f, 0.391264f,
			0.839364f, -0.427387f, 0.335869f, 0.818900f, -0.041305f, 0.572448f,
			0.719784f, 0.414997f, 0.556497f, 0.881744f, 0.450270f, 0.140659f,
			0.401823f, -0.898220f, -0.178152f, -0.054020f, 0.791344f, 0.608980f,
			-0.293774f, 0.763994f, 0.574465f, -0.450798f, 0.610347f, 0.651351f,
			-0.638221f, 0.186694f, 0.746873f, -0.872870f, -0.257127f, 0.414708f,
			-0.587257f, -0.521710f, 0.618828f, -0.353658f, -0.641974f, 0.680291f,
			0.041649f, -0.611273f, 0.790323f, 0.348342f, -0.779183f, 0.521087f,
			0.499167f, -0.622441f, 0.602826f, 0.790019f, -0.303831f, 0.532500f,
			0.660118f, 0.060733f, 0.748702f, 0.604921f, 0.294161f, 0.739960f,
			0.385697f, 0.379346f, 0.841032f, 0.239693f, 0.207876f, 0.948332f,
			0.012623f, 0.258532f, 0.965920f, -0.100557f, 0.457147f, 0.883688f,
			0.046967f, 0.628588f, 0.776319f, -0.430391f, -0.445405f, 0.785097f,
			-0.434291f, -0.196228f, 0.879139f, -0.256637f, -0.336867f, 0.905902f,
			-0.131372f, -0.158910f, 0.978514f, 0.102379f, -0.208767f, 0.972592f,
			0.195687f, -0.450129f, 0.871258f, 0.627319f, -0.423148f, 0.653771f,
			0.687439f, -0.171583f, 0.705682f, 0.275920f, -0.021255f, 0.960946f,
			0.459367f, 0.157466f, 0.874178f, 0.285395f, 0.583184f, 0.760556f,
			-0.812174f, 0.460303f, 0.358461f, -0.189068f, 0.641223f, 0.743698f,
			-0.338875f, 0.476480f, 0.811252f, -0.920994f, 0.347186f, 0.176727f,
			0.040639f, 0.024465f, 0.998874f, -0.739132f, -0.353747f, 0.573190f,
			-0.603512f, -0.286615f, 0.744060f, -0.188676f, -0.547059f, 0.815554f,
			-0.026045f, -0.397820f, 0.917094f, 0.267897f, -0.649041f, 0.712023f,
			0.518246f, -0.284891f, 0.806386f, 0.493451f, -0.066533f, 0.867225f,
			-0.328188f, 0.140251f, 0.934143f, -0.328188f, 0.140251f, 0.934143f,
			-0.328188f, 0.140251f, 0.934143f, -0.328188f, 0.140251f, 0.934143f,
		];
	}
}
