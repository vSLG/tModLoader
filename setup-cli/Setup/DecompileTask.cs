using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

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
			var decompiledLibraries = new[] { "ReLogic" };

			// Decompile embedded library sources directly into Terraria project. Treated the same as Terraria source
			foreach (var lib in decompiledLibraries) {
				var libRes = clientModule.Resources.Single(r => r.Name.EndsWith(lib + ".dll"));
				AddEmbeddedLibrary(libRes, projectDecompiler.AssemblyResolver, items);
				exclude.Add(SanitizePath(libRes.Name, clientModule));
			}

			AddModule(clientModule, projectDecompiler.AssemblyResolver, items, files, resources, exclude);

			items.Add(WriteCommonConfigurationFileFuture());
			items.Add(WriteTerrariaProjectFileFuture(clientModule, files, resources, decompiledLibraries));

			Future.ExecuteParallel(items);

			return 0;
		}

		private static string SanitizePath(string path, PEFile module) {
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

			var sources = PEUtils.SourceFiles(module, SanitizePath).Where(gr => {
				foreach (var td in gr.ToList())
					return projectDecompiler.IncludeTypeWhenDecompilingProject(module, td);
				return false;
			}).ToList();

			var resources = PEUtils.ResourceFiles(module, SanitizePath).ToList();
			var ts = new DecompilerTypeSystem(module, resolver, decompilerSettings);

			if (exclude != null) {
				sources.RemoveAll(src => exclude.Contains(src.Key));
				resources.RemoveAll(res => exclude.Contains(res.path));
			}

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

		private void WriteProjectFile(
			PEFile module,
			string outputType,
			IEnumerable<string> sources,
			IEnumerable<string> resources,
			Action<XmlTextWriter> writeSpecificConfig
		) {
			var name = PEUtils.AssemblyTitle(module);
			var filename = name + ".csproj";
			var path = Path.Combine(outputDir, name, filename);
			FSUtils.CreateParentDirectory(path);

			Console.WriteLine($"Writing project file {path}");

			using (var sw = new StreamWriter(path))
			using (var w = new XmlTextWriter(sw)) {
				w.Formatting = System.Xml.Formatting.Indented;
				w.WriteStartElement("Project");
				w.WriteAttributeString("Sdk", "Microsoft.NET.Sdk");

				w.WriteStartElement("Import");
				w.WriteAttributeString("Project", "../Configuration.targets");
				w.WriteEndElement(); // </Import>

				w.WriteStartElement("PropertyGroup");
				w.WriteElementString("OutputType", outputType);

				var attribs = PEUtils.CustomAttributes(module);
				w.WriteElementString("Version", new AssemblyName(module.FullName).Version.ToString());
				w.WriteElementString("Company", attribs[nameof(AssemblyCompanyAttribute)]);
				w.WriteElementString("Copyright", attribs[nameof(AssemblyCopyrightAttribute)]);

				w.WriteElementString("RootNamespace", module.Name);
				w.WriteEndElement(); // </PropertyGroup>

				writeSpecificConfig(w);

				// resources
				w.WriteStartElement("ItemGroup");
				foreach (var r in ApplyWildcards(resources, sources.ToArray()).OrderBy(r => r)) {
					w.WriteStartElement("EmbeddedResource");
					w.WriteAttributeString("Include", r);
					w.WriteEndElement();
				}
				w.WriteEndElement(); // </ItemGroup>
				w.WriteEndElement(); // </Project>

				sw.Write(Environment.NewLine);
			}
		}

		private Future WriteProjectFileFuture(
			PEFile module,
			string outputType,
			IEnumerable<string> sources,
			IEnumerable<string> resources,
			Action<XmlTextWriter> writeSpecificConfig
		) => new Future(() => WriteProjectFile(module, outputType, sources, resources, writeSpecificConfig));

		private IEnumerable<string> ApplyWildcards(IEnumerable<string> include, IReadOnlyList<string> exclude) {
			var wildpaths = new HashSet<string>();
			foreach (var path in include) {
				if (wildpaths.Any(path.StartsWith))
					continue;

				string wpath = path;
				string cards = "";
				while (wpath.Contains('/')) {
					var parent = wpath.Substring(0, wpath.LastIndexOf('/'));
					if (exclude.Any(e => e.StartsWith(parent)))
						break; // Can't use parent as a wildcard

					wpath = parent;
					if (cards.Length < 2)
						cards += "*";
				}

				if (wpath != path) {
					wildpaths.Add(wpath);
					yield return $"{wpath}/{cards}";
				}
				else {
					yield return path;
				}
			}
		}

		private void AddEmbeddedLibrary(Resource res, IAssemblyResolver resolver, List<Future> items) {
			using var s = res.TryOpenStream();
			s.Position = 0;

			var module = new PEFile(res.Name, s, PEStreamOptions.PrefetchEntireImage);
			var files = new HashSet<string>();
			var resources = new HashSet<string>();

			AddModule(module, resolver, items, files, resources);

			items.Add(WriteProjectFileFuture(module, "Library", files, resources, w => {
				// References
				w.WriteStartElement("ItemGroup");
				foreach (var r in module.AssemblyReferences.OrderBy(r => r.Name)) {
					if (r.Name == "mscorlib")
						continue;

					w.WriteStartElement("Reference");
					w.WriteAttributeString("Include", r.Name);
					w.WriteEndElement();
				}
				w.WriteEndElement(); // </ItemGroup>

				// TODO: resolve references to embedded terraria libraries with their HintPath
			}));
		}

		private Future WriteTerrariaProjectFileFuture(
			PEFile module,
			IEnumerable<string> sources,
			IEnumerable<string> resources,
			ICollection<string> decompiledLibraries
		) {
			return WriteProjectFileFuture(module, "Exe", sources, resources, w => {
				//configurations
				w.WriteStartElement("PropertyGroup");
				w.WriteAttributeString("Condition", "$(Configuration.Contains('Server'))");
				w.WriteElementString("OutputType", "Exe");
				w.WriteElementString("OutputName", "$(OutputName)Server");
				w.WriteEndElement(); // </PropertyGroup>

				// references
				w.WriteStartElement("ItemGroup");
				foreach (var r in module.AssemblyReferences.OrderBy(r => r.Name)) {
					if (r.Name == "mscorlib")
						continue;

					if (decompiledLibraries?.Contains(r.Name) ?? false) {
						w.WriteStartElement("ProjectReference");
						w.WriteAttributeString("Include", $"../{r.Name}/{r.Name}.csproj");
						w.WriteEndElement();

						w.WriteStartElement("EmbeddedResource");
						w.WriteAttributeString("Include", $"../{r.Name}/bin/$(Configuration)/$(TargetFramework)/{r.Name}.dll");
						w.WriteElementString("LogicalName", $"Terraria.Libraries.{r.Name}.{r.Name}.dll");
					}
					else {
						w.WriteStartElement("Reference");
						w.WriteAttributeString("Include", r.Name);
					}
					w.WriteEndElement();
				}
				w.WriteEndElement(); // </ItemGroup>

			});
		}

		private void WriteCommonConfigurationFile() {
			var filename = "Configuration.targets";
			var path = Path.Combine(outputDir, filename);
			FSUtils.CreateParentDirectory(path);

			Console.WriteLine($"Writing project file {path}");

			using (var sw = new StreamWriter(path))
			using (var w = new XmlTextWriter(sw)) {
				w.Formatting = System.Xml.Formatting.Indented;
				w.WriteStartElement("Project");

				w.WriteStartElement("PropertyGroup");
				w.WriteElementString("TargetFramework", "net40");
				w.WriteElementString("LangVersion", "8.0");
				w.WriteElementString("Configurations", "Debug;Release;ServerDebug;ServerRelease");
				w.WriteElementString("AssemblySearchPaths", "$(AssemblySearchPaths);{GAC}");
				w.WriteElementString("PlatformTarget", "x86");
				w.WriteElementString("AllowUnsafeBlocks", "true");
				w.WriteElementString("Optimize", "true");
				w.WriteEndElement(); // </PropertyGroup>

				//configurations
				w.WriteStartElement("PropertyGroup");
				w.WriteAttributeString("Condition", "$(Configuration.Contains('Server'))");
				w.WriteElementString("DefineConstants", "$(DefineConstants);SERVER");
				w.WriteEndElement(); // </PropertyGroup>

				w.WriteStartElement("PropertyGroup");
				w.WriteAttributeString("Condition", "!$(Configuration.Contains('Server'))");
				w.WriteElementString("DefineConstants", "$(DefineConstants);CLIENT");
				w.WriteEndElement(); // </PropertyGroup>

				w.WriteStartElement("PropertyGroup");
				w.WriteAttributeString("Condition", "$(Configuration.Contains('Debug'))");
				w.WriteElementString("Optimize", "false");
				w.WriteElementString("DefineConstants", "$(DefineConstants);DEBUG");
				w.WriteEndElement(); // </PropertyGroup>

				w.WriteEndElement(); // </Project>

				sw.Write(Environment.NewLine);
			}
		}

		private Future WriteCommonConfigurationFileFuture()
			=> new Future(() => WriteCommonConfigurationFile());

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
