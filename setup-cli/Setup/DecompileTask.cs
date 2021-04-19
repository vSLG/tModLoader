using System;
using System.Collections.Generic;
using System.Text;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;

namespace Terraria.ModLoader.Setup
{
	class DecompileTask
	{
		private readonly string outputDir;
		private readonly string exePath;

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

			return 0;
		}
	}
}
