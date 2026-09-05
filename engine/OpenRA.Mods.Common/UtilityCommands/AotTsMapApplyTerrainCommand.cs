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
using System.IO;
using OpenRA.FileSystem;

namespace OpenRA.Mods.Common.UtilityCommands
{
	sealed class AotTsMapApplyTerrainCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--aot-ts-apply-terrain";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 5;
		}

		[Desc("MAPFOLDER GRIDFILE WATERTEMPLATEID CLIFFTEMPLATEID",
			"Stamp a grid produced by --aot-ts-dump-terrain onto a map, scaled to fill its Bounds. Tiberium is written as ResourceType index 1.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;
			var map = new Map(modData, new Folder(args[1]));

			var waterTemplate = (ushort)int.Parse(args[3]);
			var cliffTemplate = (ushort)int.Parse(args[4]);

			var lines = File.ReadAllLines(args[2]);
			var dims = lines[0].Split(' ');
			var srcWidth = int.Parse(dims[0]);
			var srcHeight = int.Parse(dims[1]);

			var bounds = map.Bounds;
			var placedWater = 0;
			var placedCliff = 0;
			var placedTiberium = 0;

			for (var y = 0; y < bounds.Height; y++)
			{
				var sy = y * srcHeight / bounds.Height;
				if (sy >= srcHeight)
					sy = srcHeight - 1;

				var row = lines[1 + sy];

				for (var x = 0; x < bounds.Width; x++)
				{
					var sx = x * srcWidth / bounds.Width;
					if (sx >= srcWidth)
						sx = srcWidth - 1;

					var ch = row[sx];
					var uv = new MPos(bounds.X + x, bounds.Y + y);
					if (!map.Tiles.Contains(uv))
						continue;

					if (ch == 'W')
					{
						map.Tiles[uv] = new TerrainTile(waterTemplate, 0);
						placedWater++;
					}
					else if (ch == 'C')
					{
						map.Tiles[uv] = new TerrainTile(cliffTemplate, 0);
						placedCliff++;
					}

					if (ch >= '1' && ch <= '9')
					{
						map.Resources[uv] = new ResourceTile(1, (byte)(ch - '0'));
						placedTiberium++;
					}
				}
			}

			map.Save((IReadWritePackage)map.Package);
			Console.WriteLine($"Applied terrain to '{map.Title}': water={placedWater} cliff={placedCliff} tiberium={placedTiberium}");
		}
	}
}
