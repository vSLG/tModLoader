using DiffPatch;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Terraria.ModLoader.Setup
{
	class PatchTask
	{
		public readonly string PatchDir;
		public readonly string SourceDir;
		public readonly string OutputDir;
		public readonly string TmlDir;
		public readonly Patcher.Mode Mode;

		private StreamWriter logFile;
		private int failures = 0;
		private int warnings = 0;
		private int fuzzy = 0;

		public PatchTask(
			string patchDir,
			string sourceDir,
			string outputDir,
			string tmlDir,
			Patcher.Mode mode = Patcher.Mode.EXACT
		) {
			this.PatchDir = patchDir;
			this.SourceDir = sourceDir;
			this.OutputDir = outputDir;
			this.TmlDir = tmlDir;
			this.Mode = mode;
		}

		public int Run() {
			var items = new List<Future>();
			var newFiles = new HashSet<string>();
			var allPatches = Directory.EnumerateFiles(PatchDir, "*", SearchOption.AllDirectories);

			string removedFileList = Path.Combine(PatchDir, "removed_files.list");
			var noCopy = File.Exists(removedFileList) ?
						 new HashSet<string>(File.ReadAllLines(removedFileList)) :
						 new HashSet<string>();

			foreach (var file in allPatches) {
				if (file.EndsWith(".patch")) {
					items.Add(new Future(() => newFiles.Add(Patch(file).PatchedPath)));
					noCopy.Add(file.Substring(0, file.Length - 6));
				}
				else if (Path.GetFileName(file) != "removed_files.list") {
					var relativePath = Path.GetRelativePath(PatchDir, file);
					var dest = Path.Combine(OutputDir, relativePath);

					items.Add(new Future(() => {
						Console.WriteLine($"Copying {relativePath}");
						FSUtils.Copy(file, dest);
					}));

					newFiles.Add(dest);
				}
			}

			foreach (var file in EnumerateSrcFiles(SourceDir)) {
				if (noCopy.Contains(file))
					continue;

				var relativePath = Path.GetRelativePath(SourceDir, file);
				var dest = Path.Combine(OutputDir, relativePath);

				items.Add(new Future(() => {
					Console.WriteLine($"Copying {relativePath}");
					FSUtils.Copy(file, dest);
				}));

				newFiles.Add(dest);
			}

			try {
				FSUtils.CreateDirectory(Path.Combine(TmlDir, "setup-cli", "log"));
				logFile = new StreamWriter(Path.Combine(TmlDir, "setup-cli", "log", "patch.log"));
				Future.ExecuteParallel(items);
			}
			finally {
				logFile?.Close();
			}

			foreach (var file in EnumerateSrcFiles(OutputDir))
				if (!newFiles.Contains(file))
					File.Delete(file);

			return 0;
		}

		private FilePatcher Patch(string patchFile) {
			Console.WriteLine($"Patching {Path.GetRelativePath(PatchDir, patchFile)}");

			var patcher = FilePatcher.FromPatchFile(patchFile, TmlDir);
			var log = new StringBuilder();

			patcher.Patch(Mode);
			FSUtils.CreateParentDirectory(patcher.PatchedPath);
			patcher.Save();

			int exact = 0, offset = 0;

			foreach (var result in patcher.results) {
				log.AppendLine(result.Summary());

				if (!result.success) {
					failures++;
					continue;
				}

				if (result.mode == Patcher.Mode.FUZZY || result.offsetWarning)
					warnings++;
				if (result.mode == Patcher.Mode.EXACT)
					exact++;
				else if (result.mode == Patcher.Mode.OFFSET)
					offset++;
				else if (result.mode == Patcher.Mode.FUZZY)
					fuzzy++;
			}

			log.Insert(
				0,
				$"{patcher.patchFile.basePath}," +
				$"\texact: {exact},\toffset: {offset}," +
				$"\tfuzzy: {fuzzy},\tfailed: {failures}"
				+ Environment.NewLine
			);

			lock (logFile)
				logFile.Write(log.ToString());

			return patcher;
		}

		private static string[] nonSourceDirs = { "bin", "obj", ".vs" };
		public static IEnumerable<string> EnumerateSrcFiles(string dir) =>
			Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
					 .Where(f => !f.Split('/', '\\').Any(nonSourceDirs.Contains));
	}
}