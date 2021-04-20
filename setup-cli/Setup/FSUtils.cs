﻿using System.IO;

namespace Terraria.ModLoader.Setup
{
	class FSUtils
	{
		public static void CreateDirectory(string dir) {
			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);
		}

		public static void CreateParentDirectory(string path) {
			CreateDirectory(Path.GetDirectoryName(path));
		}
	}
}
