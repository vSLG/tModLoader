using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using System.Linq;
using System.Reflection.Metadata;
using System.Globalization;

namespace Terraria.ModLoader.Setup
{
	class DecompileTask
	{
		private readonly string outputDir;
		private readonly string exePath;

		private WholeProjectDecompiler projectDecompiler;

		private readonly DecompilerSettings decompilerSettings = new DecompilerSettings(LanguageVersion.Latest) {
			RemoveDeadCode = true,
			CSharpFormattingOptions = FormattingOptionsFactory.CreateKRStyle()
		};

		public DecompileTask(string exePath, string outputDir) {
			this.outputDir = outputDir;
			this.exePath = exePath;
		}

		public int Run() {
			var clientModule = PEUtils.ReadModule(exePath);
			var clientAttrs = PEUtils.CustomAttributes(clientModule);

			foreach (KeyValuePair<string, string> kvp in clientAttrs) {
				Console.WriteLine($"{kvp.Key}: {kvp.Value}");
			}

			projectDecompiler = new WholeProjectDecompiler(
				decompilerSettings,
				new UniversalAssemblyResolver(clientModule.FileName, true, clientModule.Reader.DetectTargetFrameworkId()),
				null,
				null
			);

			foreach ((string path, Resource r) in PEUtils.ResourceFiles(clientModule, GetOutputPath)) {
				Console.WriteLine(path);
			}

			return 0;
		}

		private static string GetOutputPath(string path, PEFile module) {
			if (path.EndsWith(".dll")) {
				var asmRef = module.AssemblyReferences.SingleOrDefault(r => path.EndsWith(r.Name + ".dll"));
				if (asmRef != null)
					path = Path.Combine(path.Substring(0, path.Length - asmRef.Name.Length - 5), asmRef.Name + ".dll");
			}

			var rootNamespace = PEUtils.AssemblyTitle(module);
			if (path.StartsWith(rootNamespace))
				path = path.Substring(rootNamespace.Length + 1);

			path = path.Replace("Libraries.", "Libraries/"); // lets leave the folder structure in here alone
			path = path.Replace('\\', '/');

			// . to /
			int stopFolderzingAt = path.IndexOf('/');
			if (stopFolderzingAt < 0)
				stopFolderzingAt = path.LastIndexOf('.');
			path = new StringBuilder(path).Replace(".", "/", 0, stopFolderzingAt).ToString();

			// default lang files should be called Main
			if (IsCultureFile(path))
				path = path.Insert(path.LastIndexOf('.'), "/Main");

			return path;
		}

		private static bool IsCultureFile(string path) {
			if (!path.Contains('-'))
				return false;

			try {
				CultureInfo.GetCultureInfo(Path.GetFileNameWithoutExtension(path));
				return true;
			}
			catch (CultureNotFoundException) { }
			return false;
		}
	}
}
