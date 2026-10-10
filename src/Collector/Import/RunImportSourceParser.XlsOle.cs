using System.Buffers.Binary;
using System.Text;

namespace MentorRecorder.Collector.Import;

public static partial class RunImportSourceParser
{
    /// <summary>
    /// 在 NPOI 递归建立目录树之前，用有界 FAT/目录链和迭代图遍历限制 OLE 的深度、数量和循环。
    /// 这里只校验容器，不解码 BIFF、公式、图片或宏，也不跟随外部文件。
    /// </summary>
    private static void ValidateBinaryOleContainer(byte[] bytes)
    {
        if (bytes.Length < 512) throw new InvalidDataException("XLS OLE 头部不完整。");
        int Int32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26));
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(30));
        if ((version != 3 || shift != 9) && (version != 4 || shift != 12))
            throw new InvalidDataException("XLS OLE 扇区格式无效。");
        var sectorSize = 1 << shift;
        if (bytes.Length % sectorSize != 0 || bytes.Length < sectorSize)
            throw new InvalidDataException("XLS OLE 扇区被截断。");
        var sectorCount = bytes.Length / sectorSize - 1;
        int Offset(int sector)
        {
            if (sector < 0 || sector >= sectorCount) throw new InvalidDataException("XLS OLE 扇区编号无效。");
            return (sector + 1) * sectorSize;
        }
        var fatCount = Int32(44);
        var difatCount = Int32(72);
        var slots = sectorSize / 4;
        if (fatCount <= 0 || fatCount > sectorCount || difatCount < 0 || difatCount > sectorCount ||
            (long)fatCount * slots * sectorSize > MaxInflatedZipBytes)
            throw new InvalidDataException("XLS OLE 展开大小无效或超过 64 MiB 上限。");
        var fatSectors = new List<int>();
        var allocationSectors = new HashSet<int>();
        void AddFat(int sector)
        {
            if (sector == -1) return;
            _ = Offset(sector);
            if (fatSectors.Count >= fatCount || !allocationSectors.Add(sector))
                throw new InvalidDataException("XLS OLE FAT 计数无效或存在循环。");
            fatSectors.Add(sector);
        }
        for (var i = 0; i < 109; i++) AddFat(Int32(76 + i * 4));
        var difat = Int32(68);
        for (var i = 0; i < difatCount; i++)
        {
            var offset = Offset(difat);
            if (!allocationSectors.Add(difat)) throw new InvalidDataException("XLS OLE DIFAT 存在循环。");
            for (var slot = 0; slot < slots - 1; slot++) AddFat(Int32(offset + slot * 4));
            difat = Int32(offset + sectorSize - 4);
        }
        if ((difatCount != 0 && difat != -2) || fatSectors.Count != fatCount)
            throw new InvalidDataException("XLS OLE FAT 链不完整。");
        var fat = new int[fatCount * slots];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            var offset = Offset(fatSectors[i]);
            for (var slot = 0; slot < slots; slot++) fat[i * slots + slot] = Int32(offset + slot * 4);
        }
        using var directory = new MemoryStream();
        var directorySector = Int32(48);
        var directorySectors = new HashSet<int>();
        while (directorySector != -2)
        {
            var offset = Offset(directorySector);
            if (directorySector >= fat.Length || !directorySectors.Add(directorySector) || allocationSectors.Contains(directorySector))
                throw new InvalidDataException("XLS OLE 目录链无效或存在循环。");
            // 包括根目录与最后扇区的填充条目；NPOI 不能先分配超大 property 表。
            if (directory.Length + sectorSize > 128 * (1025 + sectorSize / 128))
                throw new InvalidDataException("XLS OLE 目录超过 1024 部件上限。");
            directory.Write(bytes, offset, sectorSize);
            directorySector = fat[directorySector];
        }
        var properties = directory.ToArray();
        var propertyCount = properties.Length / 128;
        int PropertyInt(int index, int offset) => BinaryPrimitives.ReadInt32LittleEndian(properties.AsSpan(index * 128 + offset));
        var active = 0;
        long streamBytes = 0;
        for (var i = 0; i < propertyCount; i++)
        {
            var type = properties[i * 128 + 66];
            if (type == 0) continue;
            if (++active > 1025) throw new InvalidDataException("XLS OLE 目录超过 1024 部件上限。");
            if (type is not (1 or 2 or 5) || (type == 5 && i != 0))
                throw new InvalidDataException("XLS OLE 目录类型无效。");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(properties.AsSpan(i * 128 + 64));
            if (nameLength is < 2 or > 64 || nameLength % 2 != 0)
                throw new InvalidDataException("XLS OLE 目录名称无效。");
            if (type is 2 or 5)
            {
                var size = BinaryPrimitives.ReadInt64LittleEndian(properties.AsSpan(i * 128 + 120));
                streamBytes += size;
                if (size < 0 || size > bytes.Length || streamBytes > MaxInflatedZipBytes)
                    throw new InvalidDataException("XLS OLE 流长度无效或超过资源上限。");
            }
        }
        if (propertyCount == 0 || properties[66] != 5)
            throw new InvalidDataException("XLS OLE 缺少根目录。");
        var pending = new Stack<(int Index, int Depth)>();
        pending.Push((0, 0));
        var visited = new HashSet<int>();
        while (pending.TryPop(out var item))
        {
            if (item.Index == -1) continue;
            if (item.Index < 0 || item.Index >= propertyCount || properties[item.Index * 128 + 66] == 0 || !visited.Add(item.Index))
                throw new InvalidDataException("XLS OLE 目录引用无效或存在循环。");
            if (item.Depth > 16) throw new InvalidDataException("XLS OLE 目录深度超过 16 层上限。");
            pending.Push((PropertyInt(item.Index, 68), item.Depth));
            pending.Push((PropertyInt(item.Index, 72), item.Depth));
            if (properties[item.Index * 128 + 66] is 1 or 5)
                pending.Push((PropertyInt(item.Index, 76), item.Depth + 1));
        }
        if (visited.Count != active) throw new InvalidDataException("XLS OLE 包含孤立的目录部件。");
    }
}
