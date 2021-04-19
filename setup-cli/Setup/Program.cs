using System;
using System.IO;
using System.ComponentModel.DataAnnotations;
using McMaster.Extensions.CommandLineUtils;

namespace Terraria.ModLoader.Setup
{
	[Command(Name = "setup", Description = "tML code patcher & decompiler tool")]
	[Subcommand(typeof(Decompile))]
	[HelpOption("-h|--help")]
	class Program
	{
		static void Main(string[] args) => CommandLineApplication.Execute<Program>(args);

		[Option("-p|--repo-path <path>", Description = "Path to tML git repository. Defaults to current directory")]
		[DirectoryExists]
		public string TmlRepoPath { get; } = ".";

		private int OnExecute(CommandLineApplication app) {
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
		[DirectoryExists]
		public string Output { get; set; }

		private int OnExecute(CommandLineApplication app) {
			if (string.IsNullOrEmpty(Output))
				Output = Path.Combine(Parent.TmlRepoPath, "src", "decompiled");

			return new DecompileTask(TerrariaExe, Output).Run();
		}
	}
}
