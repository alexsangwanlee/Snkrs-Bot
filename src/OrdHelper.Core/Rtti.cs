using System.Text;

namespace OrdHelper.Core;

/// <summary>워크3 실행 이미지/메모리 해석 중 프로세스 없이 검증할 수 있는 순수 부분.</summary>
public static class Rtti
{
    /// <summary>메모리의 uint rawcode → "300h" (바이트 순서 그대로).</summary>
    public static string FormatRawcode(uint value) => Encoding.ASCII.GetString(BitConverter.GetBytes(value));

    /// <summary>MSVC RTTI: 타입 디스크립터 이름(+0x10) → CompleteObjectLocator → vftable(-8에 COL).</summary>
    public static HashSet<ulong> FindClassVftables(byte[] image, ulong moduleBase, string className)
    {
        var name = Encoding.ASCII.GetBytes(className + "\0");
        var typeRvas = new List<uint>();
        for (var i = image.AsSpan().IndexOf(name); i >= 0;)
        {
            if (i >= 0x10) typeRvas.Add((uint)(i - 0x10));
            var next = image.AsSpan(i + 1).IndexOf(name);
            i = next < 0 ? -1 : i + 1 + next;
        }
        var locators = new HashSet<ulong>();
        foreach (var typeRva in typeRvas)
            for (var i = 0; i + 0x18 <= image.Length; i += 4)
                if (BitConverter.ToUInt32(image, i) == 1 && BitConverter.ToUInt32(image, i + 0x0C) == typeRva &&
                    BitConverter.ToUInt32(image, i + 0x14) == (uint)i)
                    locators.Add(moduleBase + (ulong)i);
        var vftables = new HashSet<ulong>();
        if (locators.Count == 0) return vftables;
        for (var i = 0; i + 8 <= image.Length; i += 8)
            if (locators.Contains(BitConverter.ToUInt64(image, i)))
                vftables.Add(moduleBase + (ulong)i + 8);
        return vftables;
    }
}
