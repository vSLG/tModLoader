using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Terraria.ModLoader.Setup
{
	class FS
	{
		public static void CreateDirectory(string dir) {
			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);
		}

		public static void CreateParentDirectory(string path) {
			CreateDirectory(Path.GetDirectoryName(path));
		}

		public static void Copy(string from, string to) {
			CreateParentDirectory(to);

			if (File.Exists(to)) {
				File.SetAttributes(to, FileAttributes.Normal);
			}

			File.Copy(from, to, true);
		}

		public static IEnumerable<(string file, string relPath)> EnumerateRelFiles(string dir) =>
			Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
					 .Select(path => (file: path, relPath: Path.GetRelativePath(dir, path)));
	}
}