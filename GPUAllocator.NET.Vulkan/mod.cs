using Silk.NET.Vulkan;
using System.Diagnostics;
namespace GPUAllocator.NET.Vulkan
{
    using Buffer = Silk.NET.Vulkan.Buffer;
    using DedicatedBlockAllocator = GPUAllocator.NET.DedicatedBlockAllocator.DedicatedBlockAllocator;
    using FreeListAllocator = GPUAllocator.NET.FreeListAllocator.FreeListAllocator;

    public class AllocationScheme
    {
        public AllocationSchemeType SchemeType { get; }
        public Buffer Buffer;
        public Image Image;

        private AllocationScheme(AllocationSchemeType type)
        {
            SchemeType = type;
        }

        public enum AllocationSchemeType
        {
            DedicatedBuffer,
            DedicatedImage,
            GpuAllocatorManaged
        }

        /// <summary>
        /// Perform a dedicated, driver-managed allocation for the given buffer, allowing
        /// it to perform optimizations on this type of allocation.
        /// </summary>
        public static AllocationScheme DedicatedBuffer(Buffer buffer) => new AllocationScheme(AllocationSchemeType.DedicatedBuffer) { Buffer = buffer };
        /// <summary>
        /// Perform a dedicated, driver-managed allocation for the given image, allowing
        /// it to perform optimizations on this type of allocation.
        /// </summary>
        public static AllocationScheme DedicatedImage(Image image) => new AllocationScheme(AllocationSchemeType.DedicatedImage) { Image = image };
        /// <summary>
        /// The memory for this resource will be allocated and managed by gpu-allocator.
        /// </summary>
        public static AllocationScheme GpuAllocatorManaged = new AllocationScheme(AllocationSchemeType.GpuAllocatorManaged);
    }

    public struct AllocationCreateDesc
    {
        /// <summary>
        /// Name of the allocation, for tracking and debugging purposes
        /// </summary>
        public string Name;
        /// <summary>
        /// Vulkan memory requirements for an allocation
        /// </summary>
        public MemoryRequirements Requirements;
        /// <summary>
        /// Location where the memory allocation should be stored
        /// </summary>
        public MemoryLocation Location;
        /// <summary>
        /// If the resource is linear (buffer / linear texture) or a regular (tiled) texture.
        /// </summary>
        public bool Linear;
        /// <summary>
        /// Determines how this allocation should be managed.
        /// </summary>
        public AllocationScheme AllocationScheme;

        public AllocationCreateDesc(string name, MemoryRequirements requirements, MemoryLocation location, bool linear, AllocationScheme allocationScheme)
        {
            Name = name;
            Requirements = requirements;
            Location = location;
            Linear = linear;
            AllocationScheme = allocationScheme;
        }
    }

    public class AllocatorCreateDesc
    {
        public Instance Instance;
        public Device Device;
        public PhysicalDevice PhysicalDevice;
        public AllocatorDebugSettings DebugSettings;
        public bool BufferDeviceAddress;
        public AllocationSizes AllocationSizes;

        public AllocatorCreateDesc(Instance instance, Device device, PhysicalDevice physicalDevice, AllocatorDebugSettings debugSettings, bool bufferDeviceAddress, AllocationSizes allocationSizes)
        {
            Instance = instance;
            Device = device;
            PhysicalDevice = physicalDevice;
            DebugSettings = debugSettings;
            BufferDeviceAddress = bufferDeviceAddress;
            AllocationSizes = allocationSizes;
        }
    }

    /// <summary>
    /// A piece of allocated memory.
    ///
    /// Could be contained in its own individual underlying memory object or as a sub-region
    /// of a larger allocation.<br></br>
    /// 
    /// ## Example
    /// <example>
    /// <code>
    /// MemoryRequirements requirements;
    /// vk.GetBufferMemoryRequirements(device, testBuffer, out requirements);
    /// var allocationCreateInfo = new AllocationCreateDesc
    /// {
    ///   Requirements = requirements,
    ///   Location = MemoryLocation.GpuOnly,
    ///   Linear = true,
    ///   AllocationScheme = AllocationScheme.GpuAllocatorManaged,
    ///   Name = "Test allocation (Gpu Only)"
    /// };
    /// var allocation = allocator.Allocate(ref allocationCreateInfo);
    /// vk.BindBufferMemory(device, testBuffer, allocation.DeviceMemory, allocation.Offset);
    /// </code>
    /// </example>
    /// </summary>
    public class Allocation
    {
        public ulong? ChunkId { get; internal set; }
        /// <summary>
        /// Returns the offset of the allocation on the <see cref="Silk.NET.Vulkan.DeviceMemory"/>.
        /// When binding the memory to a buffer or image, this offset needs to be supplied as well.
        /// </summary>
        public ulong Offset { get; internal set; }
        /// <summary>
        /// Returns the size of the allocation.
        /// </summary>
        public ulong Size { get; internal set; }
        public int MemoryBlockIndex { get; internal set; }
        public int MemoryTypeIndex { get; internal set; }
        /// <summary>
        /// Returns the <see cref="Silk.NET.Vulkan.DeviceMemory"/> object that is backing this allocation.
        /// This memory object can be shared with multiple other allocations and shouldn't be freed (or allocated from)
        /// without this library, because that will lead to undefined behavior.
        ///
        /// The result of this property can be used to pass into <see cref="Vk.BindBufferMemory"/>,
        /// <see cref="Vk.BindImageMemory"/> etc. It is exposed for this reason. Keep in mind to also
        /// pass <see cref="Offset"/> along to those.
        /// </summary>
        public DeviceMemory DeviceMemory { get; internal set; }
        /// <summary>
        /// Returns a valid mapped pointer if the memory is host visible, otherwise it will return null.
        /// The pointer already points to the exact memory region of the suballocation, so no offset needs to be applied.
        /// </summary>
        public IntPtr? MappedPtr { get; internal set; }
        /// <summary>
        /// Returns true if this allocation is using a dedicated underlying allocation.
        /// </summary>
        public bool IsDedicatedAllocation { get; internal set; }
        public MemoryPropertyFlags MemoryProperties { get; internal set; }
        public string? Name { get; internal set; }

        static Allocation Default => new Allocation()
        {
            ChunkId = null,
            Offset = 0,
            Size = 0,
            MemoryBlockIndex = -1,
            MemoryTypeIndex = -1,
            DeviceMemory = default(DeviceMemory),
            MappedPtr = null,
            MemoryProperties = MemoryPropertyFlags.None,
            Name = null,
            IsDedicatedAllocation = false,
        };

        public bool IsNull()
        {
            return ChunkId == null;
        }
    }

    /// <summary>
    /// 表示一个具体的内存块，包括内存对象、偏移量、大小以及映射状态等信息.
    /// </summary>
    public unsafe class MemoryBlock : IDisposable
    {
        public DeviceMemory DeviceMemory { get; private set; }
        public ulong Size { get; private set; }
        public IntPtr? MappedPtr { get; private set; }
        public ISubAllocator SubAllocator;

        public MemoryBlock(
            Vk vk,
            Device device,
            ulong size,
            int memTypeIndex,
            bool mapped,
            bool bufferDeviceAddress,
            AllocationScheme allocationScheme,
            bool requiresPersonalBlock)
        {
            try
            {
                DeviceMemory deviceMemory;

                var allocInfo = new MemoryAllocateInfo
                {
                    AllocationSize = size,
                    MemoryTypeIndex = (uint)memTypeIndex
                };

                MemoryAllocateFlags allocationFlags = MemoryAllocateFlags.MemoryAllocateDeviceAddressBit;
                var flagsInfo = new MemoryAllocateFlagsInfo
                {
                    Flags = allocationFlags
                };
                // TODO(manon): Test this based on if the device has this feature enabled or not
                if (bufferDeviceAddress)
                {
                    allocInfo.PNext = &flagsInfo;
                }

                // Flag the memory as dedicated if required.
                var dedicatedMemoryInfo = new MemoryDedicatedAllocateInfo();
                switch (allocationScheme.SchemeType)
                {
                    case AllocationScheme.AllocationSchemeType.DedicatedBuffer:
                        dedicatedMemoryInfo.Buffer = allocationScheme.Buffer;
                        break;
                    case AllocationScheme.AllocationSchemeType.DedicatedImage:
                        dedicatedMemoryInfo.Image = allocationScheme.Image;
                        break;
                    case AllocationScheme.AllocationSchemeType.GpuAllocatorManaged:
                        break;
                }

                if (allocationScheme.SchemeType != AllocationScheme.AllocationSchemeType.GpuAllocatorManaged)
                {
                    allocInfo.PNext = &dedicatedMemoryInfo;
                }

                Result result = vk.AllocateMemory(device, ref allocInfo, null, out deviceMemory);
                if (result != Result.Success)
                {
                    if (result == Result.ErrorOutOfDeviceMemory)
                        throw AllocationError.OutOfMemory;
                    throw AllocationError.Internal($"Unexpected error in vkAllocateMemory: {result}");
                }

                IntPtr? mappedPtr = null;
                if (mapped)
                {
                    void* mapped_Ptr = null;
                    result = vk.MapMemory(device, deviceMemory, 0, Vk.WholeSize, MemoryMapFlags.None, &mapped_Ptr);
                    if (result != Result.Success)
                    {
                        vk.FreeMemory(device, deviceMemory, null);
                        throw AllocationError.FailedToMap($"Failed to map memory: {result}");
                    }

                    if (mapped_Ptr == null)
                    {
                        vk.FreeMemory(device, deviceMemory, null);
                        throw AllocationError.FailedToMap("Returned mapped pointer is null");
                    }

                    mappedPtr = (nint?)mapped_Ptr;
                }

                ISubAllocator subAllocator;
                if (allocationScheme.SchemeType != AllocationScheme.AllocationSchemeType.GpuAllocatorManaged || requiresPersonalBlock)
                {
                    subAllocator = new DedicatedBlockAllocator(size);
                }
                else
                {
                    subAllocator = new FreeListAllocator(size);
                }

                {
                    DeviceMemory = deviceMemory;
                    Size = size;
                    MappedPtr = mappedPtr;
                    SubAllocator = subAllocator;
#if visualizer
                    DedicatedAllocation = allocationScheme.SchemeType != AllocationScheme.AllocationSchemeType.GpuAllocatorManaged;
#endif
                }
            }
            catch (Exception ex)
            {
            }
        }

        public void Destroy(Vk vk, Device device)
        {
            if (MappedPtr != null)
            {
                vk.UnmapMemory(device, DeviceMemory);
            }

            vk.FreeMemory(device, DeviceMemory, null);
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }

    internal class MemoryType
    {
        public List<MemoryBlock> MemoryBlocks { get; internal set; }
        public MemoryPropertyFlags MemoryProperties { get; internal set; }
        public int MemoryTypeIndex { get; internal set; }
        public uint HeapIndex { get; internal set; }
        public bool Mappable { get; internal set; }
        public int ActiveGeneralBlocks { get; internal set; }
        public bool BufferDeviceAddress { get; internal set; }

        public Allocation Allocate(
            Vk vk,
            Device device,
            AllocationCreateDesc desc,
            ulong granularity,
            AllocationSizes allocationSizes)
        {
            var allocationType = desc.Linear ? AllocationType.Linear : AllocationType.NonLinear;

            var memblockSize = MemoryProperties.HasFlag(MemoryPropertyFlags.HostVisibleBit)
                ? allocationSizes.HostMemblockSize
                : allocationSizes.DeviceMemblockSize;

            var size = desc.Requirements.Size;
            var alignment = desc.Requirements.Alignment;

            var dedicatedAllocation = desc.AllocationScheme.SchemeType != AllocationScheme.AllocationSchemeType.GpuAllocatorManaged;
            var requiresPersonalBlock = size > memblockSize;

            // Create a dedicated block for large memory allocations or allocations that require dedicated memory allocations.
            if (dedicatedAllocation || requiresPersonalBlock)
            {
                var memBlock = new MemoryBlock(
                    vk,
                    device,
                    size,
                    this.MemoryTypeIndex,
                    this.Mappable,
                    this.BufferDeviceAddress,
                    desc.AllocationScheme,
                    requiresPersonalBlock
                );

                int? blockIndex = this.MemoryBlocks.FindIndex(block => block == null);
                blockIndex = blockIndex == -1 ? null : blockIndex;

                if (blockIndex.HasValue)
                {
                    this.MemoryBlocks[blockIndex.Value] = memBlock;
                }
                else
                {
                    this.MemoryBlocks.Add(memBlock);
                    blockIndex = this.MemoryBlocks.Count - 1;
                }

                var memBlockInstance = this.MemoryBlocks[blockIndex.Value] ?? throw AllocationError.Internal("Memory block must be Some");

                var (offset, chunkId) = memBlockInstance.SubAllocator.Allocate(
                    size,
                    alignment,
                    allocationType,
                    granularity,
                    desc.Name
                );

                return new Allocation
                {
                    ChunkId = chunkId,
                    Offset = offset,
                    Size = size,
                    MemoryBlockIndex = blockIndex.Value,
                    MemoryTypeIndex = this.MemoryTypeIndex,
                    DeviceMemory = memBlockInstance.DeviceMemory,
                    MappedPtr = memBlockInstance.MappedPtr,
                    MemoryProperties = this.MemoryProperties,
                    Name = desc.Name,
                    IsDedicatedAllocation = dedicatedAllocation
                };
            }

            int? emptyBlockIndex = null;
            for (int memBlockI = this.MemoryBlocks.Count - 1; memBlockI >= 0; memBlockI--)
            {
                var memBlock = this.MemoryBlocks[memBlockI];
                if (memBlock != null)
                {
                    (ulong, ulong)? allocation = memBlock.SubAllocator.Allocate(
                        size,
                        alignment,
                        allocationType,
                        granularity,
                        desc.Name
                    );

                    if (allocation != null)
                    {
                        var (offset, chunkId) = allocation.Value;
                        IntPtr? mappedPtr = memBlock.MappedPtr.HasValue
                            ? new IntPtr(memBlock.MappedPtr.Value.ToInt64() + (int)offset)
                            : null;

                        return new Allocation
                        {
                            ChunkId = chunkId,
                            Offset = offset,
                            Size = size,
                            MemoryBlockIndex = memBlockI,
                            MemoryTypeIndex = this.MemoryTypeIndex,
                            DeviceMemory = memBlock.DeviceMemory,
                            MemoryProperties = this.MemoryProperties,
                            MappedPtr = mappedPtr,
                            IsDedicatedAllocation = false,
                            Name = desc.Name
                        };
                    }
                    else
                    {
                        throw AllocationError.OutOfMemory; // Block is full, continue search.
                    }
                }
                else if (!emptyBlockIndex.HasValue)
                {
                    emptyBlockIndex = memBlockI;
                }
            }

            var newMemoryBlock = new MemoryBlock(
                vk,
                device,
                memblockSize,
                this.MemoryTypeIndex,
                this.Mappable,
                this.BufferDeviceAddress,
                desc.AllocationScheme,
                false
            );

            int newBlockIndex;
            if (emptyBlockIndex.HasValue)
            {
                this.MemoryBlocks[emptyBlockIndex.Value] = newMemoryBlock;
                newBlockIndex = emptyBlockIndex.Value;
            }
            else
            {
                this.MemoryBlocks.Add(newMemoryBlock);
                newBlockIndex = this.MemoryBlocks.Count - 1;
            }

            this.ActiveGeneralBlocks += 1;

            var newMemBlockInstance = this.MemoryBlocks[newBlockIndex] ?? throw AllocationError.Internal("Memory block must be Some");
            (ulong, ulong)? newAllocation = newMemBlockInstance.SubAllocator.Allocate(
                size,
                alignment,
                allocationType,
                granularity,
                desc.Name
            );

            if (newAllocation == null)
            {
                throw AllocationError.Internal("Allocation that must succeed failed. This is a bug in the allocator.");
            }

            var (newOffset, newChunkId) = newAllocation.Value;
            IntPtr? newMappedPtr = newMemBlockInstance.MappedPtr.HasValue
                ? new IntPtr(newMemBlockInstance.MappedPtr.Value.ToInt64() + (int)newOffset)
                : null;

            return new Allocation
            {
                ChunkId = newChunkId,
                Offset = newOffset,
                Size = size,
                MemoryBlockIndex = newBlockIndex,
                MemoryTypeIndex = this.MemoryTypeIndex,
                DeviceMemory = newMemBlockInstance.DeviceMemory,
                MemoryProperties = this.MemoryProperties,
                MappedPtr = newMappedPtr,
                Name = desc.Name,
                IsDedicatedAllocation = false
            };
        }

        public void Free(Vk vk, Allocation allocation, Device device)
        {
            var blockIdx = allocation.MemoryBlockIndex;
            MemoryBlock memBlock = this.MemoryBlocks[blockIdx];
            if (memBlock == null) throw AllocationError.Internal("Memory block must be Some.");

            memBlock.SubAllocator.Free(allocation.ChunkId.Value);

            if (memBlock.SubAllocator.IsEmpty())
            {
                if (memBlock.SubAllocator.SupportsGeneralAllocations())
                {
                    if (this.ActiveGeneralBlocks > 1)
                    {
                        var block = this.MemoryBlocks[blockIdx];
                        this.MemoryBlocks[blockIdx] = null;
                        if (block == null)
                            throw AllocationError.Internal("Memory block must be Some.");
                        block.Destroy(vk, device);

                        this.ActiveGeneralBlocks--;
                    }
                }
                else
                {
                    var block = this.MemoryBlocks[blockIdx];
                    this.MemoryBlocks[blockIdx] = null;
                    if (block == null)
                        throw AllocationError.Internal("Memory block must be Some.");
                    block.Destroy(vk, device);
                }
            }
        }
    }

    /// <summary>
    /// 分配器.
    /// </summary>
    public class Allocator : IDisposable
    {
        Vk vk;
        internal List<MemoryType> MemoryTypes { get; private set; }
        /// <summary>
        /// 设备的内存信息.
        /// </summary>
        public List<MemoryHeap> MemoryHeaps { get; private set; }
        private Device Device;
        public ulong BufferImageGranularity { get; private set; }
        public AllocatorDebugSettings DebugSettings { get; private set; }
        private AllocationSizes AllocationSizes;

        public Allocator(Vk vk, AllocatorCreateDesc desc)
        {
            this.vk = vk;
            if (desc.PhysicalDevice.Handle == 0)
            {
                throw AllocationError.InvalidAllocatorCreateDesc("AllocatorCreateDesc field `physical_device` is null.");
            }

            // 获取设备支持的内存类型和内存大小
            var memProps = vk.GetPhysicalDeviceMemoryProperties(desc.PhysicalDevice);
            var memoryTypes = memProps.MemoryTypes;
            var memoryHeaps = memProps.MemoryHeaps;

            if (desc.DebugSettings.LogMemoryInformation)
            {
                Debug.WriteLine($"memory type count: {memProps.MemoryTypeCount}, memory heap count: {memProps.MemoryHeapCount}");
                Debug.WriteLine($"");

                for (int i = 0; i < memProps.MemoryTypeCount; i++)
                {
                    var memType = memoryTypes[i];
                    var flags = memType.PropertyFlags;
                    Debug.WriteLine($"memory type[{i}]: prop flags: {flags}, heap[{memType.HeapIndex}]");
                }

                for (int i = 0; i < memProps.MemoryHeapCount; i++)
                {
                    var heap = memoryHeaps[i];
                    Debug.WriteLine($"heap[{i}] flags: {heap.Flags}, size: {heap.Size / (1024 * 1024)} MiB");
                }
            }

            // 为每个内存类型创建MemoryType
            var temporaryMemoryTypes = memoryTypes.AsSpan().Slice(0, (int)memProps.MemoryTypeCount)
                .ToArray().Select((memType, i) => new MemoryType
                {
                    MemoryBlocks = new List<MemoryBlock>(),
                    MemoryProperties = memType.PropertyFlags,
                    MemoryTypeIndex = i,
                    HeapIndex = memType.HeapIndex,
                    Mappable = memType.PropertyFlags.HasFlag(MemoryPropertyFlags.HostVisibleBit),
                    ActiveGeneralBlocks = 0,
                    BufferDeviceAddress = desc.BufferDeviceAddress
                })
                .ToList();

            var physicalDeviceProperties = vk.GetPhysicalDeviceProperties(desc.PhysicalDevice);
            var granularity = physicalDeviceProperties.Limits.BufferImageGranularity;

            this.MemoryTypes = temporaryMemoryTypes;
            this.MemoryHeaps = memoryHeaps.AsSpan().Slice(0, (int)memProps.MemoryHeapCount).ToArray().ToList();
            this.Device = desc.Device;
            this.BufferImageGranularity = granularity;
            this.DebugSettings = desc.DebugSettings;
            this.AllocationSizes = AllocationSizes.Default;
        }

        public Allocation Allocate(ref AllocationCreateDesc desc)
        {
            var size = desc.Requirements.Size;
            var alignment = desc.Requirements.Alignment;

            if (this.DebugSettings.LogAllocations)
            {
                Debug.WriteLine($"Allocating `{desc.Name}` of {size} bytes with an alignment of {alignment}.");
            }

            bool IsPowerOfTwo(ulong x)
            {
                return (x & (x - 1)) == 0;
            }

            if (size == 0 || !IsPowerOfTwo(alignment))
            {
                throw AllocationError.InvalidAllocationCreateDesc;
            }

            var memLocPreferredBits = desc.Location switch
            {
                MemoryLocation.GpuOnly => MemoryPropertyFlags.DeviceLocalBit,
                MemoryLocation.CpuToGpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.DeviceLocalBit,
                MemoryLocation.GpuToCpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit,
                _ => MemoryPropertyFlags.None
            };

            var memoryTypeIndexOpt = this.FindMemoryTypeIndex(desc.Requirements, memLocPreferredBits);

            if (!memoryTypeIndexOpt.HasValue)
            {
                var memLocRequiredBits = desc.Location switch
                {
                    MemoryLocation.GpuOnly => MemoryPropertyFlags.DeviceLocalBit,
                    MemoryLocation.CpuToGpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    MemoryLocation.GpuToCpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    _ => MemoryPropertyFlags.None
                };

                memoryTypeIndexOpt = FindMemoryTypeIndex(desc.Requirements, memLocRequiredBits);
            }

            if (!memoryTypeIndexOpt.HasValue)
            {
                throw AllocationError.NoCompatibleMemoryTypeFound;
            }

            var memoryTypeIndex = memoryTypeIndexOpt.Value;

            //Do not try to create a block if the heap is smaller than the required size (avoids validation warnings).
            var memoryType = this.MemoryTypes[memoryTypeIndex];
            if (size > this.MemoryHeaps[(int)memoryType.HeapIndex].Size)
            {
                throw AllocationError.OutOfMemory;
            }
            var allocation = memoryType.Allocate(vk, this.Device, desc, this.BufferImageGranularity, this.AllocationSizes);

            if (desc.Location == MemoryLocation.CpuToGpu && allocation == null)
            {
                var memLocPreferredBitsFallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                var memoryTypeIndexFallbackOpt = FindMemoryTypeIndex(desc.Requirements, memLocPreferredBitsFallback);

                if (!memoryTypeIndexFallbackOpt.HasValue)
                {
                    throw AllocationError.NoCompatibleMemoryTypeFound;
                }

                memoryTypeIndex = memoryTypeIndexFallbackOpt.Value;
                allocation = this.MemoryTypes[memoryTypeIndex].Allocate(vk, this.Device, desc, this.BufferImageGranularity, this.AllocationSizes);
            }

            return allocation;
        }

        public void Free(Allocation allocation)
        {
            if (DebugSettings.LogFrees)
            {
                var name = allocation.Name ?? "<null>";
                Debug.WriteLine($"Freeing `{name}`.");
            }

            if (allocation.IsNull())
            {
                return;
            }

            this.MemoryTypes[allocation.MemoryTypeIndex].Free(vk, allocation, this.Device);
        }

        public void RenameAllocation(ref Allocation allocation, string name)
        {
            allocation.Name = name;

            if (allocation.IsNull())
            {
                return;
            }

            var memoryType = this.MemoryTypes[allocation.MemoryTypeIndex];
            var memoryBlock = memoryType.MemoryBlocks[allocation.MemoryBlockIndex];

            if (memoryBlock == null)
            {
                throw AllocationError.Internal("Memory block must be Some.");
            }

            memoryBlock.SubAllocator.RenameAllocation(allocation.ChunkId, name);
        }

        public void ReportMemoryLeaks(LogLevel logLevel)
        {
            for (int memTypeIndex = 0; memTypeIndex < MemoryTypes.Count; memTypeIndex++)
            {
                var memoryType = MemoryTypes[memTypeIndex];
                for (int blockIndex = 0; blockIndex < memoryType.MemoryBlocks.Count; blockIndex++)
                {
                    var memoryBlock = memoryType.MemoryBlocks[blockIndex];
                    if (memoryBlock != null)
                    {
                        memoryBlock.SubAllocator.ReportMemoryLeaks(logLevel, memTypeIndex, blockIndex);
                    }
                }
            }
        }

        private int? FindMemoryTypeIndex(MemoryRequirements memoryReq, MemoryPropertyFlags flags)
        {
            for (int i = 0; i < MemoryTypes.Count; i++)
            {
                var memoryType = MemoryTypes[i];
                if (((1 << memoryType.MemoryTypeIndex) & memoryReq.MemoryTypeBits) != 0 && memoryType.MemoryProperties.HasFlag(flags))
                {
                    return memoryType.MemoryTypeIndex;
                }
            }

            return null;
        }

        public AllocatorReport GenerateReport()
        {
            var allocations = new List<AllocationReport>();
            var blocks = new List<MemoryBlockReport>();
            ulong totalReservedBytes = 0;

            foreach (var memoryType in MemoryTypes)
            {
                foreach (var block in memoryType.MemoryBlocks.Where(block => block != null))
                {
                    totalReservedBytes += block.Size;
                    int firstAllocation = allocations.Count;
                    allocations.AddRange(block.SubAllocator.ReportAllocations());
                    blocks.Add(new MemoryBlockReport
                    {
                        Size = block.Size,
                        Allocations = new Range(firstAllocation, allocations.Count)
                    });
                }
            }

            ulong totalAllocatedBytes = (ulong)allocations.Sum(report => (long)report.Size);

            return new AllocatorReport
            {
                Allocations = allocations,
                Blocks = blocks,
                TotalAllocatedBytes = totalAllocatedBytes,
                TotalReservedBytes = totalReservedBytes
            };
        }

        public void Dispose()
        {
            // 检查是否需要在关闭时记录内存泄漏
            if (DebugSettings.LogLeaksOnShutdown)
            {
                ReportMemoryLeaks(LogLevel.Warning);
            }

            // 释放所有剩余的内存块
            foreach (var memoryType in MemoryTypes)
            {
                for (int i = 0; i < memoryType.MemoryBlocks.Count; i++)
                {
                    var block = memoryType.MemoryBlocks[i];
                    if (block != null)
                    {
                        block.Destroy(vk, Device);
                        memoryType.MemoryBlocks[i] = null; // 将块置为空，以便GC可以回收
                    }
                }
            }
            vk = null;
        }

        ~Allocator()
        {
            Dispose();
        }
    }
}
