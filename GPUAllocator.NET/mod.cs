namespace GPUAllocator.NET
{
    public enum AllocationType
    {
        Free,
        Linear,
        NonLinear,
    }

    /// <summary>
    /// Describes an allocation in the <see cref="AllocatorReport"/>.
    /// </summary>
    public struct AllocationReport
    {
        /// <summary>
        /// The name provided to the <see cref="allocate()"/> function.
        /// </summary>
        public string Name;
        /// <summary>
        /// The offset in bytes of the allocation in its memory block.
        /// </summary>
        public ulong Offset;
        /// <summary>
        /// The size in bytes of the allocation.
        /// </summary>
        public ulong Size;
    }

    /// <summary>
    /// Describes a memory block in the <see cref="AllocatorReport">
    /// </summary>
    public struct MemoryBlockReport
    {
        /// <summary>
        /// The size in bytes of this memory block.
        /// </summary>
        public ulong Size;
        /// <summary>
        /// The range of allocations in <see cref="AllocatorReport.Allocations"> that are associated to this memory block.
        /// </summary>
        public Range Allocations;
    }

    public struct AllocatorReport
    {
        /// <summary>
        /// All live allocations, sub-allocated from memory blocks.
        /// </summary>
        public List<AllocationReport> Allocations;
        /// <summary>
        /// All memory blocks.
        /// </summary>
        public List<MemoryBlockReport> Blocks;
        /// <summary>
        /// Sum of the memory used by all allocations, in bytes.
        /// </summary>
        public ulong TotalAllocatedBytes;
        /// <summary>
        /// Sum of the memory reserved by all memory blocks including unallocated regions, in bytes.
        /// </summary>
        public ulong TotalReservedBytes;
    }

    public interface ISubAllocatorBase { }

    public interface ISubAllocator : ISubAllocatorBase
    {
        (ulong, ulong) Allocate(
            ulong size,
            ulong alignment,
            AllocationType allocationType,
            ulong granularity,
            string name
        );

        void Free(ulong? chunkId);

        void RenameAllocation(
            ulong? chunkId,
            string name
        );

        void ReportMemoryLeaks(
            LogLevel logLevel,
            int memoryTypeIndex,
            int memoryBlockIndex
        );

        List<AllocationReport> ReportAllocations();

        bool SupportsGeneralAllocations();

        ulong IsAllocated();

        /// <summary>
        /// Helper function: reports if the suballocator is empty (meaning, having no allocations).
        /// </summary>
        /// <returns></returns>
        bool IsEmpty()
        {
            return IsAllocated() == 0;
        }

        public const int VISUALIZER_TABLE_MAX_ENTRY_NAME_LEN = 40;

        public static string FmtBytes(ulong amount)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };

            int idx = 0;
            double printAmount = (double)amount;
            while (true)
            {
                if (amount < 1024)
                {
                    return string.Format("{0:F2} {1}", printAmount, suffix[idx]);
                }

                printAmount = amount / 1024.0;
                amount /= 1024;
                idx++;
            }
        }
    }
}
