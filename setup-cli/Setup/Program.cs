using McMaster.Extensions.CommandLineUtils;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;

namespace Terraria.ModLoader.Setup
{
	[Command(Name = "setup", Description = "tML code patcher & decompiler tool")]
	[Subcommand(
		typeof(Decompile),
		typeof(PatchTerraria)
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

	[Command(Description = "Applies patches to fix decompile errors")]
	[HelpOption("-h|--help")]
	class PatchTerraria
	{
		[Option(
			"-t|--terraria-source <directory>",
			Description = "Path to decompiled Terraria source directory " +
						  "(defaults to <tML repo>/src/decompiled)"
		)]
		[DirectoryExists]
		public string TerrariaSource { get; set; }

		[Option(
			"-o|--output <directory>",
			Description = "Path to write patched source files " +
						  "(defaults to <tML repo>/src/Terraria)"
		)]
		[DirectoryNotExists]
		public string Output { get; set; }

		private Program Parent { get; set; }

		private int OnExecute(CommandLineApplication app) {
			if (string.IsNullOrEmpty(TerrariaSource))
				TerrariaSource = Path.Combine(Parent.TmlRepoPath, "src", "decompiled");

			if (string.IsNullOrEmpty(Output))
				Output = Path.Combine(Parent.TmlRepoPath, "src", "Terraria");

			Console.WriteLine(TerrariaSource);
			Console.WriteLine(Output);

			return new PatchTask(
				patchDir: Path.Join(Parent.TmlRepoPath, "patches", "Terraria"),
				sourceDir: TerrariaSource,
				outputDir: Output,
				tmlDir: Parent.TmlRepoPath
			).Run();
		}
	}
}
