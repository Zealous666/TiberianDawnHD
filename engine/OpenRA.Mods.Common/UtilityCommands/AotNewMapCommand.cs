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
using OpenRA.FileSystem;
using OpenRA.Mods.Common.Terrain;
using OpenRA.Primitives;

namespace OpenRA.Mods.Common.UtilityCommands
{
	sealed class AotNewMapCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--aot-new-map";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 7;
		}

		[Desc("TILESET WIDTH HEIGHT TARGETFOLDER TITLE AUTHOR",
			"Create a new blank map folder (same defaults as the in-editor New Map dialog).")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;
			var terrainInfo = modData.DefaultTerrainInfo[args[1]];
			var width = int.Parse(args[2]);
			var height = int.Parse(args[3]);

			var map = new Map(modData, terrainInfo, new Size(width + 2, height + 2));

			var tl = new PPos(1, 1);
			var br = new PPos(width, height);
			map.SetBounds(tl, br);

			map.Title = args[5];
			map.Author = args[6];
			map.RequiresMod = modData.Manifest.Id;
			map.PlayerDefinitions = new MapPlayers(map.Rules, 0).ToMiniYaml();

			if (map.Rules.TerrainInfo is ITerrainInfoNotifyMapCreated notifyMapCreated)
				notifyMapCreated.MapCreated(map);

			var package = new Folder(args[4]);
			map.Save(package);
			Console.WriteLine($"Created '{map.Title}' ({width}x{height}, {args[1]}) at {args[4]}");
		}
	}
}
