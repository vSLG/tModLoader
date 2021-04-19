using System;
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

		private int OnExecute(CommandLineApplication app) {
			Console.WriteLine("Not implemented");
			return 1;
		}
	}
}
