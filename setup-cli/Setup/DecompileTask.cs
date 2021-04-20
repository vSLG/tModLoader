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
using System.Threading.Tasks;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.CSharp.Transforms;
using System.Reflection.PortableExecutable;

namespace Terraria.ModLoader.Setup
{
	class DecompileTask
	{
		private readonly string outputDir;
		private readonly string exePath;

		private ExtendedProjectDecompiler projectDecompiler;

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

			projectDecompiler = new ExtendedProjectDecompiler(
				decompilerSettings,
				new EmbeddedAssemblyResolver(clientModule, clientModule.Reader.DetectTargetFrameworkId())
			);

			var items = new List<Future>();
			var files = new HashSet<string>();
			var resources = new HashSet<string>();
			var exclude = new List<string>();

			foreach (var lib in new[] { "ReLogic" }) {
				// TODO
			}

			AddModule(clientModule, projectDecompiler.AssemblyResolver, items, files, resources, exclude);

			Future.ExecuteParallel(items);

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

		private void DecompileSource(
			DecompilerTypeSystem ts,
			IGrouping<string, TypeDefinitionHandle> src,
			string projectName
		) {
			var path = Path.Combine(outputDir, projectName, src.Key);
			FSUtils.CreateParentDirectory(path);
			Console.WriteLine("Decompiling " + path);

			using (var w = new StringWriter()) {
				CreateDecompiler(ts)
					.DecompileTypes(src.ToArray())
					.AcceptVisitor(new CSharpOutputVisitor(w, decompilerSettings.CSharpFormattingOptions));

				File.WriteAllText(path, w.ToString());
			}
		}

		private Future DecompileSourceFuture(
			DecompilerTypeSystem ts,
			IGrouping<string, TypeDefinitionHandle> src,
			string projectName
		) => new Future(() => DecompileSource(ts, src, projectName));

		private CSharpDecompiler CreateDecompiler(DecompilerTypeSystem ts) {
			var decompiler = new CSharpDecompiler(ts, projectDecompiler.Settings);
			decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
			decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
			return decompiler;
		}

		private DecompilerTypeSystem AddModule(
			PEFile module,
			IAssemblyResolver resolver,
			List<Future> items,
			ISet<string> sourceSet,
			ISet<string> resourceSet,
			ICollection<string> exclude = null
		) {
			var projectDir = PEUtils.AssemblyTitle(module);

			var sources = PEUtils.SourceFiles(module, GetOutputPath).Where(gr => {
				foreach (var td in gr.ToList())
					return projectDecompiler.IncludeTypeWhenDecompilingProject(module, td);
				return false;
			}).ToList();

			var resources = PEUtils.ResourceFiles(module, GetOutputPath).ToList();
			var ts = new DecompilerTypeSystem(module, projectDecompiler.AssemblyResolver, decompilerSettings);

			items.AddRange(sources
				.Where(src => sourceSet.Add(src.Key))
				.Select(src => DecompileSourceFuture(ts, src, projectDir)));

			items.AddRange(resources
				.Where(res => resourceSet.Add(res.path))
				.Select(res => ExtractResourceFuture(res.path, res.r, projectDir)));

			return ts;
		}

		private void ExtractResource(string name, Resource res, string projectDir) {
			var path = Path.Combine(outputDir, projectDir, name);
			FSUtils.CreateParentDirectory(path);

			var s = res.TryOpenStream();
			s.Position = 0;
			using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
				s.CopyTo(fs);
		}

		private Future ExtractResourceFuture(string name, Resource res, string projectDir)
			=> new Future(() => ExtractResource(name, res, projectDir));


		private class EmbeddedAssemblyResolver : IAssemblyResolver
		{
			private readonly PEFile baseModule;
			private readonly UniversalAssemblyResolver _resolver;
			private readonly Dictionary<string, PEFile> cache = new Dictionary<string, PEFile>();

			public EmbeddedAssemblyResolver(PEFile baseModule, string targetFramework) {
				this.baseModule = baseModule;
				_resolver = new UniversalAssemblyResolver(baseModule.FileName, true, targetFramework, streamOptions: PEStreamOptions.PrefetchMetadata);
				_resolver.AddSearchDirectory(Path.GetDirectoryName(baseModule.FileName));
			}

			public PEFile Resolve(IAssemblyReference name) {
				lock (this) {
					if (cache.TryGetValue(name.FullName, out var module))
						return module;

					// Look in the base module's embedded resources
					var resName = name.Name + ".dll";
					var res = baseModule.Resources.Where(r => r.ResourceType == ResourceType.Embedded).SingleOrDefault(r => r.Name.EndsWith(resName));

					if (res != null)
						module = new PEFile(res.Name, res.TryOpenStream());

					if (module == null)
						module = _resolver.Resolve(name);

					cache[name.FullName] = module;
					return module;
				}
			}

			public Task<PEFile> ResolveAsync(IAssemblyReference reference) {
				return System.Threading.Tasks.Task.Run(() => Resolve(reference));
			}

			public PEFile ResolveModule(PEFile mainModule, string moduleName) => _resolver.ResolveModule(mainModule, moduleName);

			public Task<PEFile> ResolveModuleAsync(PEFile mainModule, string moduleName) => ((IAssemblyResolver)_resolver).ResolveModuleAsync(mainModule, moduleName);
		}

		private class ExtendedProjectDecompiler : WholeProjectDecompiler
		{
			public ExtendedProjectDecompiler(
				DecompilerSettings settings,
				IAssemblyResolver assemblyResolver,
				AssemblyReferenceClassifier assemblyReferenceClassifier = null,
				IDebugInfoProvider debugInfoProvider = null
			) : base(settings, assemblyResolver, assemblyReferenceClassifier, debugInfoProvider) { }

			public new bool IncludeTypeWhenDecompilingProject(PEFile module, TypeDefinitionHandle type) => base.IncludeTypeWhenDecompilingProject(module, type);
		}
	}
}
