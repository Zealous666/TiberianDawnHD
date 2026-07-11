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
using System.Text;
using OpenRA.FileSystem;

namespace OpenRA.Mods.Common.UtilityCommands
{
	sealed class AotTsMapDumpTerrainCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--aot-ts-dump-terrain";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 3;
		}

		[Desc("MAPFOLDER OUTFILE", "Dump a compact water/cliff/tiberium grid (within the map Bounds) to a text file.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;
			var map = new Map(modData, new Folder(args[1]));
			var bounds = map.Bounds;

			var sb = new StringBuilder();
			sb.Append(bounds.Width).Append(' ').Append(bounds.Height).Append('\n');

			for (var y = 0; y < bounds.Height; y++)
			{
				for (var x = 0; x < bounds.Width; x++)
				{
					var uv = new MPos(bounds.X + x, bounds.Y + y);
					var c = '.';

					if (map.Resources.Contains(uv))
					{
						var res = map.Resources[uv];
						if (res.Type != 0)
						{
							var level = 1 + (res.Index * 8 / 11);
							if (level > 9)
								level = 9;
							if (level < 1)
								level = 1;
							c = (char)('0' + level);
						}
					}

					if (c == '.' && map.Tiles.Contains(uv))
					{
						var terrainType = map.GetTerrainInfo(uv).Type;
						if (terrainType == "Water")
							c = 'W';
						else if (terrainType == "Cliff")
							c = 'C';
					}

					sb.Append(c);
				}

				sb.Append('\n');
			}

			File.WriteAllText(args[2], sb.ToString());
			Console.WriteLine($"Wrote {bounds.Width}x{bounds.Height} terrain grid from '{map.Title}' to {args[2]}");
		}
	}
}
