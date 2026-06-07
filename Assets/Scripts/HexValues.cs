using System.IO;
using UnityEngine;

/// <summary>
/// Values that describe the contents of a cell.
/// </summary>
[System.Serializable]
public struct HexValues
{
	/// <summary>
	/// Six values stored in 32 bits.
	/// TTTTTTTT WWWWWWSS SSSSHHWW WWWEEEEE.
	/// T(errain), W(ood), S(tone), (H)ome, W(ater), E(levation)
	/// </summary>
	/// <remarks>Not readonly to support hot reloading in Unity.</remarks>
#pragma warning disable IDE0044 // Add readonly modifier
	int values;
#pragma warning restore IDE0044 // Add readonly modifier

	readonly int Get(int mask, int shift) =>
		(int)((uint)values >> shift) & mask;

	readonly HexValues With(int value, int mask, int shift) => new()
	{
		values = (values & ~(mask << shift)) | ((value & mask) << shift)
	};

	public readonly int Elevation => Get(31, 0) - 15;

	public readonly HexValues WithElevation(int value) =>
		With(value + 15, 31, 0);

	public readonly int WaterLevel => Get(31, 5);

	public readonly int ViewElevation => Mathf.Max(Elevation, WaterLevel);

	public readonly bool IsUnderwater => WaterLevel > Elevation;

	public readonly HexValues WithWaterLevel(int value) => With(value, 31, 5);
	
	public readonly int HomeLevel => Get(3, 10);

	public readonly HexValues WithHomeLevel(int value) => With(value, 3, 10);

	public readonly int StoneLevel => Get(63, 12);

	public readonly HexValues WithStoneLevel(int value) => With(value, 63, 12);

	public readonly int WoodLevel => Get(63, 18);

	public readonly HexValues WithWoodLevel(int value) => With(value, 63, 18);
	
	public readonly int TerrainTypeIndex => Get(255, 24);
	
	public readonly HexValues WithTerrainTypeIndex(int index) =>
		With(index, 255, 24);

	/// <summary>
	/// Save the values.
	/// </summary>
	/// <param name="writer"><see cref="BinaryWriter"/> to use.</param>
	public readonly void Save(BinaryWriter writer)
	{
		writer.Write((byte)TerrainTypeIndex);
		writer.Write((byte)(Elevation + 127));
		writer.Write((byte)WaterLevel);
		writer.Write((byte)HomeLevel);
		writer.Write((byte)StoneLevel);
		writer.Write((byte)WoodLevel);
	}

	/// <summary>
	/// Load the values.
	/// </summary>
	/// <param name="reader"><see cref="BinaryReader"/> to use.</param>
	/// <param name="header">Header version.</param>
	public static HexValues Load(BinaryReader reader, int header)
	{
		HexValues values = default;
		values = values.WithTerrainTypeIndex(reader.ReadByte());
		int elevation = reader.ReadByte();
		if (header >= 4)
		{
			elevation -= 127;
		}
		values = values.WithElevation(elevation);
		values = values.WithWaterLevel(reader.ReadByte());
		values = values.WithHomeLevel(reader.ReadByte());
		values = values.WithStoneLevel(reader.ReadByte());
        return values.WithWoodLevel(reader.ReadByte());
	}
}
