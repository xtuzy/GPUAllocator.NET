using System.Diagnostics;

namespace GPUAllocator.NET.DedicatedBlockAllocator
{
    public class DedicatedBlockAllocator : ISubAllocator
    {
        private ulong size;
        private ulong allocated;
        private string? name;

        public DedicatedBlockAllocator(ulong size)
        {
            this.size = size;
            this.allocated = 0;
            this.name = null;
        }

        #region Impliment Interface
        public (ulong, ulong) Allocate(
            ulong size,
            ulong alignment,
            AllocationType allocationType,
            ulong granularity,
            string name
        )
        {
            if (this.allocated != 0)
            {
                throw AllocationError.OutOfMemory;
            }

            if (this.size != size)
            {
                throw AllocationError.Internal("DedicatedBlockAllocator size must match allocation size.");
            }

            this.allocated = size;
            this.name = name;

            // Dummy ID
            ulong dummyId = 1;
            return (0, dummyId);
        }

        public void Free(ulong? chunkId)
        {
            if (chunkId != 1)
            {
                throw AllocationError.Internal("Chunk ID must be 1.");
            }
            else
            {
                this.allocated = 0;
            }
        }

        public void RenameAllocation(ulong? chunkId, string name)
        {
            if (chunkId != 1)
            {
                throw AllocationError.Internal("Chunk ID must be 1.");
            }
            else
            {
                this.name = name;
            }
        }

        public void ReportMemoryLeaks(LogLevel logLevel, int memoryTypeIndex, int memoryBlockIndex)
        {
            string name = this.name ?? string.Empty;

            // Log the memory leak
            Debug.WriteLine($"Leak detected:\n\tMemory type: {memoryTypeIndex}\n\tMemory block: {memoryBlockIndex}\n\tDedicated allocation: {{\n\t\tSize: {this.size},\n\t\tName: {name}\n\t}}");
        }

        public List<AllocationReport> ReportAllocations()
        {
            return new List<AllocationReport>
            {
                new AllocationReport
                {
                    Name = this.name ?? "<Unnamed Dedicated allocation>",
                    Size = this.size,
                    // Add backtrace if required
                }
            };
        }
        
        public bool SupportsGeneralAllocations()
        {
            return false;
        }

        public ulong IsAllocated()
        {
            return this.allocated;
        }

        public bool IsEmpty()
        {
            return this.IsAllocated() == 0;
        }
        #endregion
    }
}
