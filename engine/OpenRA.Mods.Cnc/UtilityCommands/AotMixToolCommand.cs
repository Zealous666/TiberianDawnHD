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
using System.Linq;
using OpenRA.Mods.Cnc.FileFormats;
using OpenRA.Mods.Cnc.FileSystem;

namespace OpenRA.Mods.Cnc.UtilityCommands
{
	sealed class AotMixToolCommand : IUtilityCommand
	{
		string IUtilityCommand.Name => "--aot-mix";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			// --aot-mix list  MIXPATH
			// --aot-mix extract MIXPATH INNERNAME OUTFILE
			return args.Length >= 3;
		}

		[Desc("(list MIXPATH) | (extract MIXPATH INNERNAME OUTFILE)",
			"List or extract files from a (possibly nested/encrypted) Westwood MIX using the global mix database.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;

			// Load global mix database from the engine dir.
			string[] globalFilenames = [];
			var dbPath = Path.Combine(Platform.EngineDir, "global mix database.dat");
			if (File.Exists(dbPath))
			{
				using var dbStream = File.OpenRead(dbPath);
				using var db = new XccGlobalDatabase(dbStream);
				globalFilenames = db.Entries.ToHashSet().ToArray();
			}
			else
				Console.WriteLine($"WARNING: global mix database.dat not found at {dbPath}");

			var mode = args[1];
			var mixPath = args[2];
			var stream = File.OpenRead(mixPath);
			var mix = new MixLoader.MixFile(stream, mixPath, globalFilenames);

			if (mode == "list")
			{
				var names = mix.Contents.OrderBy(x => x).ToList();
				Console.WriteLine($"# {mixPath}: {names.Count} resolved entries");
				foreach (var n in names)
					Console.WriteLine(n);
			}
			else if (mode == "extract")
			{
				if (args.Length < 5)
					throw new ArgumentException("extract needs MIXPATH INNERNAME OUTFILE");

				var inner = args[3];
				var outFile = args[4];
				using var content = mix.GetStream(inner);
				if (content == null)
					throw new FileNotFoundException($"'{inner}' not found in {mixPath}");

				using var outStream = File.Create(outFile);
				content.CopyTo(outStream);
				Console.WriteLine($"Extracted {inner} -> {outFile}");
			}
			else
				throw new ArgumentException($"Unknown mode '{mode}'");
		}
	}
}
