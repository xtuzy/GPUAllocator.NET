using System.Diagnostics;

namespace GPUAllocator.NET
{
    public enum MemoryLocation
    {
        /// <summary>
        /// The allocated resource is stored at an unknown memory location; let the driver decide what's the best location
        /// </summary>
        Unknown,
        /// <summary>
        /// Store the allocation in GPU only accessible memory - typically this is the faster GPU resource and this should be
        /// where most of the allocations live.
        /// </summary>
        GpuOnly,
        /// <summary>
        /// Memory useful for uploading data to the GPU and potentially for constant buffers
        /// </summary>
        CpuToGpu,
        /// <summary>
        /// Memory useful for CPU readback of data
        /// </summary>
        GpuToCpu,
    }

    public struct AllocatorDebugSettings
    {
        /// <summary>
        /// Logs out debugging information about the various heaps the current device has on startup
        /// </summary>
        public bool LogMemoryInformation;
        /// <summary>
        /// Logs out all memory leaks on shutdown with log level Warn
        /// </summary>
        public bool LogLeaksOnShutdown;
        /// <summary>
        /// Stores a copy of the full backtrace for every allocation made, this makes it easier to debug leaks
        /// or other memory allocations, but storing stack traces has a RAM overhead so should be disabled
        /// in shipping applications.
        /// </summary>
        //public bool StoreStackTraces;
        /// <summary>
        /// Log out every allocation as it's being made with log level Debug, rather spammy so off by default
        /// </summary>
        public bool LogAllocations;
        /// <summary>
        /// Log out every free that is being called with log level Debug, rather spammy so off by default
        /// </summary>
        public bool LogFrees;
        /// <summary>
        /// Log out stack traces when either `log_allocations` or `log_frees` is enabled.
        /// </summary>
        //public bool LogStackTraces;
    }

    /// <summary>
    /// The sizes of the memory blocks that the allocator will create.
    ///
    /// Useful for tuning the allocator to your application's needs. For example most games will be fine with the default
    /// values, but eg. an app might want to use smaller block sizes to reduce the amount of memory used.
    ///
    /// Clamped between 4MB and 256MB, and rounds up to the nearest multiple of 4MB for alignment reasons.
    /// </summary>
    public struct AllocationSizes
    {
        public static AllocationSizes Default = new AllocationSizes(256 * 1024 * 1024, 64 * 1024 * 1024);
        
        /// <summary>
        /// The size of the memory blocks that will be created for the GPU only memory type.
        ///
        /// Defaults to 256MB.
        /// </summary>
        public uint DeviceMemblockSize;
        /// <summary>
        /// The size of the memory blocks that will be created for the CPU visible memory types.
        ///
        /// Defaults to 64MB.
        /// </summary>
        public uint HostMemblockSize;

        public AllocationSizes(uint device_memblock_size, uint host_memblock_size)
        {
            uint FOUR_MB = 4 * 1024 * 1024;
            uint TWO_HUNDRED_AND_FIFTY_SIX_MB = 256 * 1024 * 1024;

            device_memblock_size = Math.Clamp(device_memblock_size, FOUR_MB, TWO_HUNDRED_AND_FIFTY_SIX_MB);
            host_memblock_size = Math.Clamp(host_memblock_size, FOUR_MB, TWO_HUNDRED_AND_FIFTY_SIX_MB);

            if (device_memblock_size % FOUR_MB != 0)
            {
                var val = device_memblock_size / FOUR_MB + 1;
                device_memblock_size = val * FOUR_MB;
                Debug.WriteLine($"Device memory block size must be a multiple of 4MB, clamping to {device_memblock_size / 1024 / 1024} MB");
            }

            if (host_memblock_size % FOUR_MB != 0)
            {
                var val = host_memblock_size / FOUR_MB + 1;
                host_memblock_size = val * FOUR_MB;
                Debug.WriteLine($"Host memory block size must be a multiple of 4MB, clamping to {host_memblock_size / 1024 / 1024} MB");
            }

            this.DeviceMemblockSize = device_memblock_size;
            this.HostMemblockSize = host_memblock_size;
        }
    }
}
