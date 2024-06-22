using GPUAllocator.NET.Vulkan;
using NUnit.Framework;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Buffer = Silk.NET.Vulkan.Buffer;
namespace GPUAllocator.NET.Test
{
    [TestFixture]
    public class MemoryLocationTests
    {
        private readonly Vk vk = Vk.GetApi();
        PhysicalDevice physicalDevice;
        Device device;
        Allocator allocator;

        [OneTimeSetUp]
        public unsafe void SetUp()
        {
            // Create Vulkan instance
            Instance instance;
            {
                var appInfo = new ApplicationInfo()
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = (byte*)SilkMarshal.StringToPtr("Vulkan gpu-allocator test"),
                    ApplicationVersion = new Version32(0, 0, 0),
                    PEngineName = (byte*)SilkMarshal.StringToPtr("Vulkan gpu-allocator test"),
                    EngineVersion = new Version32(0, 0, 0),
                    ApiVersion = Vk.Version10
                };

                var createInfo = new InstanceCreateInfo()
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                };

                var EnableValidationLayers = false;
                if (EnableValidationLayers)
                {
                    var layerNames = new[] { "VK_LAYER_KHRONOS_validation" };

                    createInfo.EnabledLayerCount = (uint)layerNames.Length;
                    createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(layerNames);
                }
                else
                {
                    createInfo.EnabledLayerCount = 0;
                    createInfo.PNext = null;
                }

                if (vk.CreateInstance(&createInfo, null, out instance) != Result.Success)
                {
                    Debug.Fail("Instance creation error.");
                    return;
                }

                SilkMarshal.Free((nint)appInfo.PApplicationName);
                SilkMarshal.Free((nint)appInfo.PEngineName);
                if (EnableValidationLayers)
                {
                    SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
                }
            }

            // Look for Vulkan physical device
            uint queueFamilyIndex;
            {
                uint deviceCount = 0;
                vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);

                var physicalDevices = new PhysicalDevice[deviceCount];
                fixed (PhysicalDevice* pPhysicalDevices = physicalDevices)
                {
                    vk.EnumeratePhysicalDevices(instance, ref deviceCount, pPhysicalDevices);
                }
                if (deviceCount == 0)
                {
                    Debug.Fail("Physical device error.");
                }

                (physicalDevice, queueFamilyIndex) = physicalDevices.SelectMany(pdev =>
                {
                    uint queueCount = 0;
                    vk.GetPhysicalDeviceQueueFamilyProperties(pdev, ref queueCount, null);
                    var queueProperties = new QueueFamilyProperties[queueCount];
                    fixed (QueueFamilyProperties* pQueueProperties = queueProperties)
                    {
                        vk.GetPhysicalDeviceQueueFamilyProperties(pdev, ref queueCount, pQueueProperties);
                    }

                    return queueProperties
                        .Select((props, index) => (props, index))
                        .Where(t => (t.props.QueueFlags & QueueFlags.QueueGraphicsBit) != 0)
                        .Select(t => (pdev, (uint)t.index));
                })
                .FirstOrDefault();

                if (physicalDevice.Handle == 0)
                {
                    Debug.Fail("Couldn't find suitable physical device.");
                    return;
                }
            }

            // Create Vulkan device
            {
                string[] deviceExtensionNames = { };
                var features = new PhysicalDeviceFeatures()
                {
                    ShaderClipDistance = Vk.True
                };

                var priorities = stackalloc float[] { 1.0f };

                var queueCreateInfo = new DeviceQueueCreateInfo()
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = queueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = priorities
                };

                var deviceCreateInfo = new DeviceCreateInfo()
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueCreateInfo,
                    EnabledExtensionCount = (uint)deviceExtensionNames.Length,
                    PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensionNames),
                    PEnabledFeatures = &features
                };

                if (vk.CreateDevice(physicalDevice, &deviceCreateInfo, null, out device) != Result.Success)
                {
                    Debug.Fail("Failed to create Vulkan device.");
                    return;
                }
            }

            Debug.WriteLine("Vulkan setup completed successfully.");

            // Remember to free allocated unmanaged memory
            var ptr = SilkMarshal.StringArrayToPtr(new[] { "VK_LAYER_KHRONOS_validation" });
            SilkMarshal.Free((nint)ptr);

            var allocatorCreateInfo = new AllocatorCreateDesc
            (
                instance,
                device,
                physicalDevice,
                new AllocatorDebugSettings() { LogMemoryInformation = true, LogAllocations = true, LogFrees = true, LogLeaksOnShutdown = true },
                false,
                new AllocationSizes(1024 * 1024 * 24, 1024 * 1024 * 24)
            );

            allocator = new Allocator(vk, allocatorCreateInfo);
        }

        [Test]
        public unsafe void GpuOnlyTest()
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 512,
                Usage = BufferUsageFlags.BufferUsageStorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };

            Buffer testBuffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out testBuffer);

            MemoryRequirements requirements;
            vk.GetBufferMemoryRequirements(device, testBuffer, out requirements);

            var allocationCreateInfo = new AllocationCreateDesc
            {
                Requirements = requirements,
                Location = MemoryLocation.GpuOnly,
                Linear = true,
                AllocationScheme = AllocationScheme.GpuAllocatorManaged,
                Name = "Test allocation (Gpu Only)"
            };

            var allocation = allocator.Allocate(ref allocationCreateInfo);
            vk.BindBufferMemory(device, testBuffer, allocation.DeviceMemory, allocation.Offset);

            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation);
            allocator.ReportMemoryLeaks(LogLevel.Info);

            vk.DestroyBuffer(device, testBuffer, null);

            Debug.WriteLine("Allocation and deallocation of GpuOnly memory was successful.");
        }

        [Test]
        public unsafe void GpuOnlyTestDedicated()
        {
            // Buffer1
            ushort[] indicesData = new ushort[]
            {
                0, 1, 2, 2, 3, 0
            };
            ulong bufferSize = (ulong)(Unsafe.SizeOf<ushort>() * indicesData.Length);

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 512,
                Usage = BufferUsageFlags.BufferUsageStorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };

            Buffer test1Buffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out test1Buffer);

            MemoryRequirements requirements1;
            vk.GetBufferMemoryRequirements(device, test1Buffer, out requirements1);

            var allocationCreateInfo1 = new AllocationCreateDesc
            {
                Requirements = requirements1,
                Location = MemoryLocation.GpuOnly,
                Linear = true,
                AllocationScheme = AllocationScheme.DedicatedBuffer(test1Buffer),
                Name = "Test allocation (Gpu Only)"
            };

            var allocation1 = allocator.Allocate(ref allocationCreateInfo1);
            vk.BindBufferMemory(device, test1Buffer, allocation1.DeviceMemory, allocation1.Offset);

            void* data;
            var result =  vk!.MapMemory(device, allocation1.DeviceMemory, 0, bufferSize, 0, &data);
            if (result == Result.Success)
            {
                indicesData.AsSpan().CopyTo(new Span<ushort>(data, indicesData.Length));
                vk!.UnmapMemory(device, allocation1.DeviceMemory);
            }

            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation1);
            allocator.ReportMemoryLeaks(LogLevel.Info);

            vk.DestroyBuffer(device, test1Buffer, null);

            Debug.WriteLine("Allocation and deallocation of GpuOnly memory was successful.");
        }
        
        [Test]
        public unsafe void MultipleGpuOnlyBufferTest()
        {
            // Buffer1
            ushort[] indicesData = new ushort[]
            {
                0, 1, 2, 2, 3, 0
            };
            ulong bufferSize = (ulong)(Unsafe.SizeOf<ushort>() * indicesData.Length);

            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 512,
                Usage = BufferUsageFlags.BufferUsageStorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };

            Buffer test1Buffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out test1Buffer);

            MemoryRequirements requirements1;
            vk.GetBufferMemoryRequirements(device, test1Buffer, out requirements1);

            var allocationCreateInfo1 = new AllocationCreateDesc
            {
                Requirements = requirements1,
                Location = MemoryLocation.GpuOnly,
                Linear = true,
                AllocationScheme = AllocationScheme.GpuAllocatorManaged,
                Name = "Test allocation (Gpu Only)"
            };

            var allocation1 = allocator.Allocate(ref allocationCreateInfo1);
            vk.BindBufferMemory(device, test1Buffer, allocation1.DeviceMemory, allocation1.Offset);

            void* data;
            var result =  vk!.MapMemory(device, allocation1.DeviceMemory, 0, bufferSize, 0, &data);
            if (result == Result.Success)
            {
                indicesData.AsSpan().CopyTo(new Span<ushort>(data, indicesData.Length));
                vk!.UnmapMemory(device, allocation1.DeviceMemory);
            }

            // Buffer2
            Buffer test2Buffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out test2Buffer);

            MemoryRequirements requirements2;
            vk.GetBufferMemoryRequirements(device, test2Buffer, out requirements2);

            var allocationCreateInfo2 = new AllocationCreateDesc
            {
                Requirements = requirements2,
                Location = MemoryLocation.GpuOnly,
                Linear = true,
                AllocationScheme = AllocationScheme.GpuAllocatorManaged,
                Name = "Test allocation (Gpu Only)"
            };

            var allocation2 = allocator.Allocate(ref allocationCreateInfo2);
            vk.BindBufferMemory(device, test2Buffer, allocation2.DeviceMemory, allocation2.Offset);

            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation1);
            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation2);
            allocator.ReportMemoryLeaks(LogLevel.Info);

            vk.DestroyBuffer(device, test1Buffer, null);
            vk.DestroyBuffer(device, test2Buffer, null);

            Debug.WriteLine("Allocation and deallocation of GpuOnly memory was successful.");
        }

        [Test]
        public unsafe void CpuToGpuTest()
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 512,
                Usage = BufferUsageFlags.BufferUsageStorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };

            Buffer testBuffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out testBuffer);

            MemoryRequirements requirements;
            vk.GetBufferMemoryRequirements(device, testBuffer, out requirements);

            var allocationCreateInfo = new AllocationCreateDesc
            {
                Requirements = requirements,
                Location = MemoryLocation.CpuToGpu,
                Linear = true,
                AllocationScheme = AllocationScheme.GpuAllocatorManaged,
                Name = "Test allocation (Cpu to Gpu)"
            };

            var allocation = allocator.Allocate(ref allocationCreateInfo);

            vk.BindBufferMemory(device, testBuffer, allocation.DeviceMemory, allocation.Offset);

            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation);
            allocator.ReportMemoryLeaks(LogLevel.Info);

            vk.DestroyBuffer(device, testBuffer, null);

            Debug.WriteLine("Allocation and deallocation of CpuToGpu memory was successful.");
        }

        [Test]
        public unsafe void GpuToCpuTest()
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = 512,
                Usage = BufferUsageFlags.BufferUsageStorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };

            Buffer testBuffer;
            vk.CreateBuffer(device, in bufferCreateInfo, null, out testBuffer);

            MemoryRequirements requirements;
            vk.GetBufferMemoryRequirements(device, testBuffer, out requirements);

            var allocationCreateInfo = new AllocationCreateDesc
            {
                Requirements = requirements,
                Location = MemoryLocation.GpuToCpu,
                Linear = true,
                AllocationScheme = AllocationScheme.GpuAllocatorManaged,
                Name = "Test allocation (Gpu to Cpu)"
            };

            var allocation = allocator.Allocate(ref allocationCreateInfo);

            vk.BindBufferMemory(device, testBuffer, allocation.DeviceMemory, allocation.Offset);

            allocator.ReportMemoryLeaks(LogLevel.Info);
            allocator.Free(allocation);
            allocator.ReportMemoryLeaks(LogLevel.Info);

            vk.DestroyBuffer(device, testBuffer, null);

            Debug.WriteLine("Allocation and deallocation of GpuToCpu memory was successful.");
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            allocator.Dispose();
            vk.Dispose();
        }
    }
}
