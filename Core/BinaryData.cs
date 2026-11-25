namespace MeshControl.Core;

/// <summary>
/// Les protocoles KVM/relais de MeshCentral sont big-endian sur le fil.
/// </summary>
public static class BinaryData
{
    public static ushort? BeUInt16(this byte[] data, int offset)
    {
        if (data.Length < offset + 2) return null;
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    public static uint? BeUInt32(this byte[] data, int offset)
    {
        if (data.Length < offset + 4) return null;
        uint v = 0;
        for (int i = 0; i < 4; i++) v = (v << 8) | data[offset + i];
        return v;
    }

    public static void AppendBE(this List<byte> data, ushort value)
    {
        data.Add((byte)(value >> 8));
        data.Add((byte)(value & 0xFF));
    }

    public static void AppendBE(this List<byte> data, uint value)
    {
        data.Add((byte)((value >> 24) & 0xFF));
        data.Add((byte)((value >> 16) & 0xFF));
        data.Add((byte)((value >> 8) & 0xFF));
        data.Add((byte)(value & 0xFF));
    }
}
