using McMaster.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace Terraria.ModLoader.Setup
{
	[Command(Name = "setup", Description = "tML code patcher & decompiler tool")]
	[Subcommand(
		typeof(Decompile),
		typeof(Patch)
	)]
	[HelpOption("-h|--help")]
	class Program
	{
		static void Main(string[] args) => CommandLineApplication.Execute<Program>(args);

		[Option("-p|--repo-path <path>", Description = "Path to tML git repository. Defaults to current directory")]
		[DirectoryExists]
		public string TmlRepoPath { get; } = ".";

		private int OnExecute(CommandLineApplication app) {
			// TODO: TUI
			app.ShowHelp();
			return 1;
		}
	}

	[Command(Description = "Decompiles Terraria into <tML repo>/src/decompiled")]
	[HelpOption("-h|--help")]
	class Decompile
	{
		private Program Parent { get; set; }

		[Option("-e|--terraria-exe <path>", Description = "Path to Terraria executable file (.exe)")]
		[FileExists]
		[Required]
		public string TerrariaExe { get; }

		[Option("-o|--output <directory>", Description = "Path to output directory (defaults to <tML repo>/src/decompiled)")]
		[DirectoryNotExists]
		public string Output { get; set; }

		private int OnExecute(CommandLineApplication app) {
			if (string.IsNullOrEmpty(Output))
				Output = Path.Combine(Parent.TmlRepoPath, "src", "decompiled");

			return new DecompileTask(TerrariaExe, Output).Run();
		}
	}

	[Command(
		Description = "Applies patches to fix decompile errors. " +
						 "If no options specified, patch both Terraria and tModLoader."
	)]
	[HelpOption("-h|--help")]
	class Patch
	{
		[Option("-t", Description = "Patch Terraria")]
		public bool PatchTerraria { get; set; }

		[Option("-tml", Description = "Patch tModLoader")]
		public bool PatchTModLoader { get; set; }

		private Program Parent { get; set; }

		private int OnExecute(CommandLineApplication app) {
			if (!(PatchTerraria || PatchTModLoader))
				PatchTerraria = PatchTModLoader = true;

			var projects = new List<string>();

			if (PatchTerraria)
				projects.Add("Terraria");

			if (PatchTModLoader)
				projects.Add("tModLoader");

			int ret = 0;

			foreach (var project in projects)
				ret += new PatchTask(
					patchDir: Path.Join(Parent.TmlRepoPath, "patches", project),
					sourceDir: Path.Join(Parent.TmlRepoPath, "src", project == "Terraria" ? "decompiled" : "Terraria"),
					outputDir: Path.Join(Parent.TmlRepoPath, "src", project),
					tmlDir: Parent.TmlRepoPath
				).Run();

			return ret;
		}
	}
}
