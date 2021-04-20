using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Metadata;
using System.Reflection;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;

namespace Terraria.ModLoader.Setup
{
	class PEUtils
	{
		static public string AssemblyTitle(PEFile module) => CustomAttributes(module)[nameof(AssemblyTitleAttribute)];

		static public IDictionary<string, string> CustomAttributes(PEFile module) {
			var dict = new Dictionary<string, string>();

			var reader = module.Reader.GetMetadataReader();
			var attribs = reader.GetAssemblyDefinition().GetCustomAttributes().Select(reader.GetCustomAttribute);
			foreach (var attrib in attribs) {
				var ctor = reader.GetMemberReference((MemberReferenceHandle)attrib.Constructor);
				var attrTypeName = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name);
				if (!knownAttributes.Contains(attrTypeName))
					continue;

				var value = attrib.DecodeValue(new IDGAFAttributeTypeProvider());
				dict[attrTypeName] = value.FixedArguments.Single().Value as string;
			}

			return dict;
		}

		static public PEFile ReadModule(string path) {
			Console.WriteLine("Loading " + Path.GetFileName(path));

			using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read)) {
				return new PEFile(path, fileStream, PEStreamOptions.PrefetchEntireImage);
			}
		}

		static public IEnumerable<IGrouping<string, TypeDefinitionHandle>> SourceFiles(PEFile module, Func<string, PEFile, string> pathParser = null) {
			var metadata = module.Metadata;

			return metadata.GetTopLevelTypeDefinitions().GroupBy(i => {
				var type = metadata.GetTypeDefinition(i);
				var path = WholeProjectDecompiler.CleanUpFileName(metadata.GetString(type.Name)) + ".cs";
				string ns = metadata.GetString(type.Namespace);

				if (!string.IsNullOrEmpty(ns))
					path = Path.Combine(WholeProjectDecompiler.CleanUpFileName(ns), path);

				if (pathParser != null)
					path = pathParser(path, module);

				return path;
			});
		}

		static public IEnumerable<(string path, Resource r)> ResourceFiles(PEFile module, Func<string, PEFile, string> pathParser = null) {
			return module.Resources.Where(r => r.ResourceType == ResourceType.Embedded).Select(res => {
				if (pathParser != null)
					return (pathParser(res.Name, module), res);
				return (res.Name, res);
			});
		}

		private static string[] knownAttributes = {
			nameof(AssemblyCompanyAttribute),
			nameof(AssemblyCopyrightAttribute),
			nameof(AssemblyTitleAttribute),
			nameof(AssemblyFileVersionAttribute),
		};

		private class IDGAFAttributeTypeProvider : ICustomAttributeTypeProvider<object>
		{
			public object GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
			public object GetSystemType() => throw new NotImplementedException();
			public object GetSZArrayType(object elementType) => throw new NotImplementedException();
			public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw new NotImplementedException();
			public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => throw new NotImplementedException();
			public object GetTypeFromSerializedName(string name) => throw new NotImplementedException();
			public PrimitiveTypeCode GetUnderlyingEnumType(object type) => throw new NotImplementedException();
			public bool IsSystemType(object type) => throw new NotImplementedException();
		}
	}
}
