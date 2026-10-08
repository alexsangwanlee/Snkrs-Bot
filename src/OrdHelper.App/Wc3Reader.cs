// 워크래프트3 읽기 전용 패 인식.
// 알고리즘은 asmond-lab/onepiece-random-defense-overlay(MIT)의 StructuralUnitPoolScanner /
// WarcraftMemoryRecognitionService를 최소한으로 옮긴 것이다.
//   1) 모듈 이미지에서 RTTI ".?AVCUnit@@" vftable을 찾는다.
//   2) 힙을 한 번 훑어 CUnit 객체와 (개수, 포인터 배열) 구조체 후보를 모은다.
//   3) 소유자가 가장 다양한 배열을 전역 유닛 풀로 고른다.
//   4) 풀에서 내 슬롯 유닛의 rawcode를 센다.
// 메모리 쓰기·주입·키 입력은 하지 않는다 (PROCESS_VM_READ | PROCESS_QUERY_INFORMATION 만 사용).
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OrdHelper.Core;

namespace OrdHelper.App;

public sealed record Wc3Status(string Message, IReadOnlyDictionary<string, int>? Hand, bool Error = false);

/// <summary>빌드별 유닛 풀 배치. 오프셋은 참고 저장소 memory-profiles.json / 3.0 실측 문서 기준.</summary>
internal sealed record Wc3Layout(string Version, bool Verified, int CountOffset, int EntriesOffset,
    int OwnerOffset = 448, int RawcodeOffset = 376,
    long LocalRootA = 0, long LocalRootB = 0, ulong LocalRootXor = 0, int LocalIdOffset = 0);

public sealed class Wc3Reader : IDisposable
{
    private static readonly Wc3Layout[] Layouts =
    [
        new("2.0.4.23745", true, 2968, 2976, LocalRootA: 45455716, LocalRootB: 45972424,
            LocalRootXor: 0x363ABBFD3DEFAEC9, LocalIdOffset: 9828),
        new("3.0.0.24268", false, 3080, 3088),
    ];
    private const string ProcessName = "Warcraft III";
    private const int MaximumUnits = 8191;
    // 풀 후보로 볼 최소 유닛 수. 낮추면 힙의 우연한 (개수, 포인터) 쌍이 폭증한다.
    private const int MinimumUnits = 8;

    private readonly GameData _data;
    private int _processId = -1;
    private ProcessMemory? _memory;
    private ulong _moduleBase;
    private int _moduleSize;
    private string _version = "";
    private Wc3Layout? _layout;
    private HashSet<ulong>? _vftables;
    private ulong _root;
    private int _emptyReads;
    private DateTime _nextScan = DateTime.MinValue;
    private int _errors;
    // 스캔 스레드마다 16MB 버퍼 하나를 계속 쓴다 (작업 단위마다 새로 만들지 않게).
    [ThreadStatic] private static byte[]? _scanBuffer;

    public Wc3Reader(GameData data) => _data = data;

    /// <param name="slot">0~3 (1P~4P). null이면 빌드가 지원할 때 자동, 아니면 1P.</param>
    public Wc3Status Read(byte? slot)
    {
        try
        {
            if (!Attach()) return new("워크3 실행 안 됨", null);
            if (_root == 0)
            {
                if (DateTime.UtcNow < _nextScan) return new($"워크3 {_version} · 게임 대기 중", null);
                // 전체 힙 스캔은 무겁다: 대기 중에는 15초에 한 번만.
                _nextScan = DateTime.UtcNow.AddSeconds(15);
                if (!LocateRoot()) return new($"워크3 {_version} · 게임 대기 중 (유닛 없음)", null);
            }
            var local = slot ?? TryLocalSlot() ?? 0;
            // 읽는 사이 유닛이 생기거나 없어지면 다시 읽는다 (전체 재탐색은 무거우니 하지 않는다).
            Dictionary<string, int>? hand = null;
            for (var attempt = 0; hand is null; attempt++)
            {
                try { hand = ReadHand(local); }
                catch (ListChangedException) when (attempt < 3) { }
            }
            // 판이 바뀌면 풀 위치가 바뀔 수 있다. 계속 비어 있으면 다시 찾는다.
            _emptyReads = hand.Count == 0 ? _emptyReads + 1 : 0;
            if (_emptyReads > 10) { _root = 0; _emptyReads = 0; }
            _errors = 0;
            var tag = _layout!.Verified ? "" : " · 미검증 빌드(실험)";
            return new($"워크3 {_version} · {local + 1}P · 유닛 {hand.Values.Sum()}{tag}", hand);
        }
        catch (Exception e) when (e is Win32Exception or InvalidDataException or InvalidOperationException
                                      or OverflowException or ArgumentOutOfRangeException)
        {
            _root = 0;
            // 연속으로 실패하면 프로세스에 처음부터 다시 붙는다 (게임 재시작·맵 변경 대응).
            if (++_errors >= 3) Reset();
            return new($"워크3 읽기 실패, 다시 연결 중: {e.Message}", null, true);
        }
    }

    private bool Attach()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            var process = processes.MaxBy(p => p.StartTime);
            if (process is null) { Detach(); return false; }
            if (process.Id == _processId && _memory is not null) return true;
            Detach();
            var module = process.MainModule ?? throw new InvalidOperationException("모듈 정보를 읽을 수 없습니다.");
            _memory = ProcessMemory.Open(process.Id);
            _processId = process.Id;
            _moduleBase = (ulong)module.BaseAddress;
            _moduleSize = module.ModuleMemorySize;
            _version = module.FileVersionInfo.FileVersion?.Replace(", ", ".") ?? "?";
            _nextScan = DateTime.MinValue;
            return true;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>다음 읽기에서 프로세스·유닛 풀을 처음부터 다시 찾는다.</summary>
    public void Reset() => Detach();

    private void Detach()
    {
        _memory?.Dispose();
        _memory = null;
        _processId = -1;
        _root = 0;
        _vftables = null;
        _errors = 0;
        _nextScan = DateTime.MinValue;
    }

    private bool LocateRoot()
    {
        var vftables = _vftables ??= Rtti.FindClassVftables(ReadImage(), _moduleBase, ".?AVCUnit@@");
        if (vftables.Count == 0)
        {
            _vftables = null;
            throw new InvalidOperationException("CUnit 클래스를 찾지 못했습니다.");
        }
        // 알려진 빌드는 그 배치만, 모르는 빌드는 알려진 배치를 차례로 시도한다.
        var known = Layouts.Where(l => l.Version == _version).ToList();
        foreach (var layout in known.Count > 0 ? known : Layouts.Reverse())
        {
            var root = FindPool(layout, vftables);
            if (root == 0) continue;
            _layout = known.Count > 0 ? layout : layout with { Verified = false };
            _root = root;
            return true;
        }
        return false;
    }

    private byte? TryLocalSlot()
    {
        var l = _layout!;
        if (l.LocalRootA == 0) return null;
        ulong Root() => _memory!.ReadUInt64(_moduleBase + (ulong)l.LocalRootA) ^ l.LocalRootXor ^
                        _memory.ReadUInt64(_moduleBase + (ulong)l.LocalRootB);
        var before = Root();
        if (!ProcessMemory.IsUserAddress(before)) return null;
        var slot = BitConverter.ToUInt16(_memory!.Read(before + (ulong)l.LocalIdOffset, 2));
        return Root() == before && slot <= 3 ? (byte)slot : null;
    }

    private Dictionary<string, int> ReadHand(byte slot)
    {
        var l = _layout!;
        var count = _memory!.ReadInt32(_root + (ulong)l.CountOffset);
        var entries = _memory.ReadUInt64(_root + (ulong)l.EntriesOffset);
        if (count is < 0 or > MaximumUnits) throw new InvalidDataException($"유닛 수 이상: {count}");
        var hand = new Dictionary<string, int>(StringComparer.Ordinal);
        if (count == 0) return hand;
        var pointers = _memory.Read(entries, count * 8);
        for (var i = 0; i < count; i++)
        {
            var unit = BitConverter.ToUInt64(pointers, i * 8);
            if (!ProcessMemory.IsUserAddress(unit)) continue;
            if (_memory.ReadByte(unit + (ulong)l.OwnerOffset) != slot) continue;
            var code = _data.Canonical(Rtti.FormatRawcode(_memory.ReadUInt32(unit + (ulong)l.RawcodeOffset)));
            // 맵 컨트롤러 등 카드가 아닌 유닛은 데이터에 있는 rawcode만 남겨 걸러낸다.
            if (_data.Units.TryGetValue(code, out var known) && !known.IsWildcard)
                hand[code] = hand.GetValueOrDefault(code) + 1;
        }
        if (_memory.ReadInt32(_root + (ulong)l.CountOffset) != count)
            throw new ListChangedException();
        return hand;
    }

    // ── 구조 스캔 ─────────────────────────────────────────────

    private ulong FindPool(Wc3Layout layout, HashSet<ulong> vftables)
    {
        var units = new Dictionary<ulong, byte>();
        var structs = new List<(ulong Address, int Count, ulong Entries)>();
        var gate = new object();
        // 수 GB 힙을 훑는다. 게임이 끊기지 않게 스레드 2개·낮은 우선순위·버퍼 재사용
        // (원랜디 조합도우미의 "연동 시 렉/튕김" 문제를 피하려는 것).
        Parallel.ForEach(_memory!.PrivateRegions(), new ParallelOptions { MaxDegreeOfParallelism = 2 },
            () =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                return (Units: new Dictionary<ulong, byte>(), Structs: new List<(ulong, int, ulong)>(),
                    Buffer: _scanBuffer ??= new byte[ProcessMemory.ChunkBytes]);
            },
            (region, _, local) =>
            {
                foreach (var (chunk, length) in _memory.ReadChunks(region, local.Buffer))
                    ScanBuffer(chunk, local.Buffer, length, layout, vftables, local.Units, local.Structs);
                return local;
            },
            local =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
                lock (gate)
                {
                    foreach (var pair in local.Units) units[pair.Key] = pair.Value;
                    structs.AddRange(local.Structs);
                }
            });
        if (units.Count < MinimumUnits) return 0;

        // 전역 풀: 서로 다른 CUnit을 담고, 소유자 종류가 가장 많은 배열. 동률이면 실패.
        var ranked = new List<(ulong Address, int Owners, int Hits, int Count, ulong Entries)>();
        foreach (var (address, count, entries) in structs.DistinctBy(c => (c.Entries, c.Count)))
        {
            var bytes = _memory.ReadAvailable(entries, count * 8);
            if (bytes.Length < count * 8) continue;
            var seen = new HashSet<ulong>();
            var owners = new HashSet<byte>();
            var sound = true;
            for (var i = 0; i < count && sound; i++)
            {
                var value = BitConverter.ToUInt64(bytes, i * 8);
                if (value == 0) continue;
                if (!ProcessMemory.IsUserAddress(value)) sound = false;
                else if (units.TryGetValue(value, out var owner) && seen.Add(value)) owners.Add(owner);
            }
            if (sound && seen.Count >= MinimumUnits) ranked.Add((address, owners.Count, seen.Count, count, entries));
        }
        // 8바이트씩 훑으면 같은 배열을 가리키는 어긋난 후보가 생긴다: 배열마다 슬롯이 가장 적은 것만 남긴다.
        var best = ranked.GroupBy(r => r.Entries)
            .Select(g => g.OrderByDescending(r => r.Hits).ThenBy(r => r.Count).First())
            .OrderByDescending(r => r.Owners).ThenByDescending(r => r.Hits).ThenBy(r => r.Count)
            .Take(2).ToList();
        if (best.Count == 0) return 0;
        if (best.Count > 1 && best[0].Owners == best[1].Owners && best[0].Hits == best[1].Hits)
            throw new InvalidOperationException("유닛 목록 후보가 구분되지 않습니다.");
        return best[0].Address;
    }

    /// <summary>오프셋이 모두 8의 배수라 qword 배열로 훑는다. 이 루프가 스캔 시간 대부분.</summary>
    private static void ScanBuffer(ulong chunk, byte[] buffer, int length, Wc3Layout layout, HashSet<ulong> vftables,
        Dictionary<ulong, byte> units, List<(ulong, int, ulong)> structs)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(0, length & ~7));
        var countWord = layout.CountOffset / 8;
        var entriesWord = layout.EntriesOffset / 8;
        var unitLimit = (length - layout.OwnerOffset - 1) / 8;
        for (var i = 0; i < words.Length; i++)
        {
            var value = words[i];
            if (value != 0 && i <= unitLimit && vftables.Contains(value))
                units[chunk + (ulong)i * 8] = buffer[i * 8 + layout.OwnerOffset];
            if (i + entriesWord >= words.Length) continue;
            var count = (int)(uint)words[i + countWord];
            if (count < MinimumUnits || count > MaximumUnits) continue;
            var entries = words[i + entriesWord];
            if (!ProcessMemory.IsUserAddress(entries) || (entries & 7) != 0) continue;
            structs.Add((chunk + (ulong)i * 8, count, entries));
        }
    }

    private byte[] ReadImage()
    {
        var image = new byte[_moduleSize];
        foreach (var (start, size) in _memory!.ModuleRegions(_moduleBase, _moduleSize))
        for (ulong offset = 0; offset < size; offset += 0x1000)
        {
            var length = (int)Math.Min(0x1000, size - offset);
            var page = _memory.ReadAvailable(start + offset, length);
            Buffer.BlockCopy(page, 0, image, (int)(start + offset - _moduleBase), page.Length);
        }
        return image;
    }

    public void Dispose() => Detach();

    private sealed class ListChangedException() : InvalidOperationException("읽는 중 유닛 목록이 바뀌었습니다.");
}

/// <summary>ReadProcessMemory / VirtualQueryEx 래퍼. 읽기 권한만 요청한다.</summary>
internal sealed class ProcessMemory : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private ProcessMemory(SafeProcessHandle handle) => _handle = handle;

    public static ProcessMemory Open(int processId)
    {
        var handle = OpenProcess(0x0010 | 0x0400, false, processId); // VM_READ | QUERY_INFORMATION
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "워크3 프로세스를 열 수 없습니다. 워크3가 관리자 권한이면 이 프로그램도 관리자로 실행하세요.");
        return new ProcessMemory(handle);
    }

    public static bool IsUserAddress(ulong address) => address is >= 0x10000 and <= 0x00007FFFFFFFFFFF;

    public byte[] Read(ulong address, int count)
    {
        if (!IsUserAddress(address)) throw new InvalidDataException($"비정상 주소 0x{address:X}");
        var bytes = new byte[count];
        if (!ReadProcessMemory(_handle, (nint)address, bytes, count, out var read) || read != count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"메모리 읽기 실패 0x{address:X}");
        return bytes;
    }

    public byte[] ReadAvailable(ulong address, int count)
    {
        var bytes = new byte[count];
        if (!IsUserAddress(address)) return [];
        ReadProcessMemory(_handle, (nint)address, bytes, count, out var read);
        var actual = (int)Math.Clamp(read, 0, count);
        if (actual != count) Array.Resize(ref bytes, actual);
        return bytes;
    }

    public byte ReadByte(ulong address) => Read(address, 1)[0];
    public int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
    public uint ReadUInt32(ulong address) => BitConverter.ToUInt32(Read(address, 4));
    public ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));

    /// <summary>커밋된 읽기 가능 전용(힙) 영역.</summary>
    public IEnumerable<(ulong Base, ulong Size)> PrivateRegions() =>
        Regions(0x10000, 0x00007FFFFFFFFFFF).Where(r => r.Type == 0x20000).Select(r => (r.Base, r.Size));

    /// <summary>모듈 이미지 범위의 읽기 가능 영역 (일부 .text는 NOACCESS라 건너뛴다).</summary>
    public IEnumerable<(ulong Base, ulong Size)> ModuleRegions(ulong moduleBase, int moduleSize) =>
        Regions(moduleBase, moduleBase + (ulong)moduleSize).Select(r =>
        {
            var start = Math.Max(r.Base, moduleBase);
            return (start, Math.Min(r.Base + r.Size, moduleBase + (ulong)moduleSize) - start);
        });

    public const int ChunkBytes = 16 * 1024 * 1024;

    /// <summary>영역을 buffer 크기 조각으로 읽어 (시작 주소, 읽은 길이)를 돌려준다. buffer는 호출자가 재사용.</summary>
    public IEnumerable<(ulong Base, int Length)> ReadChunks((ulong Base, ulong Size) region, byte[] buffer)
    {
        const int overlap = 0x2000;
        for (ulong position = 0; position < region.Size;)
        {
            var length = (int)Math.Min((ulong)buffer.Length, region.Size - position);
            var read = ReadInto(region.Base + position, buffer, length);
            if (read >= 0x1000) yield return (region.Base + position, read);
            position += (ulong)Math.Max(length - overlap, 0x1000);
        }
    }

    private int ReadInto(ulong address, byte[] buffer, int count)
    {
        if (!IsUserAddress(address)) return 0;
        ReadProcessMemory(_handle, (nint)address, buffer, count, out var read);
        return (int)Math.Clamp(read, 0, count);
    }

    private IEnumerable<(ulong Base, ulong Size, uint Type)> Regions(ulong from, ulong to)
    {
        for (var address = from; address < to;)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0)
                yield break;
            var start = (ulong)info.BaseAddress;
            var size = (ulong)info.RegionSize;
            if (size == 0 || start + size <= address) yield break;
            if (info.State == 0x1000 && IsReadable(info.Protect) && IsUserAddress(start))
                yield return (start, size, info.Type);
            address = start + size;
        }
    }

    private static bool IsReadable(uint protect) =>
        (protect & 0x101) == 0 && (protect & 0xFF) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, int size,
        out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info,
        nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
