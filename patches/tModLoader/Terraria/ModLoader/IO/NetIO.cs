using System.IO;

namespace Terraria.ModLoader.IO
{
	public static class NetIO
	{
		/// <summary> Writes the value as a byte if ModNet.AllowVanillaClients is true, otherwise, writes it as a VarInt. </summary>
		public static void WriteExtendedVanillaByte(this BinaryWriter writer, int value) {
			if (ModNet.AllowVanillaClients)
				writer.Write((byte)value);
			else
				writer.WriteVarInt(value);
		}

		/// <summary> Reads a byte value if ModNet.AllowVanillaClients is true, otherwise, reads a VarInt. </summary>
		public static int ReadExtendedVanillaByte(this BinaryReader reader)
			=> ModNet.AllowVanillaClients ? reader.ReadByte() : reader.ReadVarInt();
	}
}
