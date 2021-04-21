using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using System;
using System.IO;
using System.Linq;
using Terraria.ModLoader.Setup.Formatting;

namespace Terraria.ModLoader.Setup
{
	class FormatTask
	{
		private static AdhocWorkspace workspace = new AdhocWorkspace();
		static FormatTask() {
			//FixRoslynFormatter.Apply();

			workspace.Options = workspace.Options
				.WithChangedOption(new OptionKey(FormattingOptions.UseTabs, LanguageNames.CSharp), true)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInMethods, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInProperties, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAccessors, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousMethods, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInControlBlocks, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInAnonymousTypes, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInObjectCollectionArrayInitializers, false)
				.WithChangedOption(CSharpFormattingOptions.NewLinesForBracesInLambdaExpressionBody, false);
		}

		private static string projectPath; //persist across executions

		public void Run() {
			var dir = Path.GetDirectoryName(projectPath); //just format all files in the directory
			var workItems = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
				.Select(path => new FileInfo(path))
				.OrderByDescending(f => f.Length)
				.Select(f => new Future(() => FormatFile(f.FullName, false)));


			Future.ExecuteParallel(workItems.ToList());
		}

		public static void FormatFile(string path, bool aggressive) {
			string source = File.ReadAllText(path);
			string formatted = Format(source, aggressive);
			if (source != formatted)
				File.WriteAllText(path, formatted);
		}

		public static SyntaxNode Format(SyntaxNode node, bool aggressive) {
			if (aggressive) {
				node = new NoNewlineBetweenFieldsRewriter().Visit(node);
				node = new RemoveBracesFromSingleStatementRewriter().Visit(node);
			}

			node = new AddVisualNewlinesRewriter().Visit(node);
			node = Formatter.Format(node, workspace);
			node = new CollectionInitializerFormatter().Visit(node);
			return node;
		}

		public static string Format(string source, bool aggressive) {
			var tree = CSharpSyntaxTree.ParseText(source);
			return Format(tree.GetRoot(), aggressive).ToFullString();
		}
	}
}
