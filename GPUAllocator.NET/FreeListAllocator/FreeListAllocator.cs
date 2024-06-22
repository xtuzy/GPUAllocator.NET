using System.Collections.Concurrent;
using System.Diagnostics;
namespace GPUAllocator.NET.FreeListAllocator
{
    public class MemoryChunk
    {
        public ulong ChunkId { get; set; }
        public ulong Size { get; set; }
        public ulong Offset { get; set; }
        public AllocationType AllocationType { get; set; }
        public string? Name { get; set; }
        public ulong? Prev { get; set; }
        public ulong? Next { get; set; }
    }

    class Util
    {
        internal static ulong AlignDown(ulong val, ulong alignment)
        {
            return val & ~(alignment - 1UL);
        }

        internal static ulong AlignUp(ulong val, ulong alignment)
        {
            return AlignDown(val + alignment - 1UL, alignment);
        }

        /// <summary>
        /// Test if two suballocations will overlap the same page.
        /// </summary>
        internal static bool IsOnSamePage(ulong offsetA, ulong sizeA, ulong offsetB, ulong pageSize)
        {
            var endA = offsetA + sizeA - 1;
            var endPageA = AlignDown(endA, pageSize);
            var startB = offsetB;
            var startPageB = AlignDown(startB, pageSize);

            return endPageA == startPageB;
        }

        /// <summary>
        /// Test if two allocation types will be conflicting or not.
        /// </summary>
        internal static bool HasGranularityConflict(AllocationType type0, AllocationType type1)
        {
            if (type0 == AllocationType.Free || type1 == AllocationType.Free)
            {
                return false;
            }

            return type0 != type1;
        }
    }

    public class FreeListAllocator : ISubAllocator
    {
        private const bool USE_BEST_FIT = true;
        private ulong size;
        private ulong allocated;
        private ulong chunkIdCounter;
        private ConcurrentDictionary<ulong, MemoryChunk> chunks;
        private ConcurrentDictionary<ulong, MemoryChunk> freeChunks;

        public FreeListAllocator(ulong size)
        {
            this.size = size;
            this.allocated = 0;
            // 0 is not allowed as a chunk ID, 1 is used by the initial chunk, next chunk is going to be 2.
            // The system well take the counter as the ID, and the increment the counter.
            this.chunkIdCounter = 2;

            var initialChunkId = 1UL;
            this.chunks = new ConcurrentDictionary<ulong, MemoryChunk>();
            this.chunks.TryAdd(initialChunkId, new MemoryChunk
            {
                ChunkId = initialChunkId,
                Size = size,
                Offset = 0,
                AllocationType = AllocationType.Free,
                Prev = null,
                Next = null
            });

            this.freeChunks = new ConcurrentDictionary<ulong, MemoryChunk>();
            this.freeChunks.TryAdd(initialChunkId, this.chunks[initialChunkId]);
        }

        /// <summary>
        /// Generates a new unique chunk ID
        /// </summary>
        /// <returns></returns>
        private ulong GetNewChunkId()
        {
            if (this.chunkIdCounter == ulong.MaxValue)
            {
                throw AllocationError.OutOfMemory;
            }

            var id = this.chunkIdCounter;
            this.chunkIdCounter += 1;
            if (id == 0)
                throw AllocationError.Internal("New chunk id was 0, which is not allowed.");
            return id;
        }

        /// <summary>
        /// Finds the specified `chunk_id` in the list of free chunks and removes if from the list
        /// </summary>
        /// <param name="chunkId"></param>
        private void RemoveIdFromFreeList(ulong chunkId)
        {
            this.freeChunks.TryRemove(chunkId, out _);
        }

        /// <summary>
        /// Merges two adjacent chunks. Right chunk will be merged into the left chunk
        /// </summary>
        /// <param name="chunkLeft"></param>
        /// <param name="chunkRight"></param>
        private void MergeFreeChunks(ulong chunkLeft, ulong chunkRight)
        {
            // Gather data from right chunk and remove it
            if (!this.chunks.TryRemove(chunkRight, out var rightChunk))
                throw AllocationError.Internal("Chunk ID not present in chunk list.");
            this.RemoveIdFromFreeList(rightChunk.ChunkId);
            var rightSize = rightChunk.Size;
            var rightNext = rightChunk.Next;

            // Merge into left chunk
            if (!this.chunks.ContainsKey(chunkLeft))
                throw AllocationError.Internal("Chunk ID not present in chunk list.");
            var leftChunk = this.chunks[chunkLeft];
            leftChunk.Next = rightNext;
            leftChunk.Size += rightSize;

            // Patch pointers
            if (rightNext.HasValue)
            {
                if (!this.chunks.ContainsKey(rightNext.Value))
                    throw AllocationError.Internal("Chunk ID not present in chunk list.");
                var nextChunk = this.chunks[rightNext.Value];
                nextChunk.Prev = chunkLeft;
            }
        }

        #region Impliment Interface
        public (ulong, ulong) Allocate(ulong size, ulong alignment, AllocationType allocationType, ulong granularity, string name)
        {
            var freeSize = this.size - this.allocated;
            if (size > freeSize)
            {
                throw AllocationError.OutOfMemory;
            }

            ulong? bestFitId = null;
            ulong bestOffset = 0;
            ulong bestAlignedSize = 0;
            ulong bestChunkSize = 0;

            foreach (var currentChunkId in this.freeChunks.Keys)
            {
                if (!this.chunks.ContainsKey(currentChunkId))
                    throw AllocationError.Internal("Chunk ID in free list is not present in chunk list.");
                var currentChunk = this.chunks[currentChunkId];
                if (currentChunk.Size < size)
                {
                    continue;
                }

                var offset = Util.AlignUp(currentChunk.Offset, alignment);

                if (currentChunk.Prev.HasValue)
                {
                    if (!this.chunks.ContainsKey(currentChunk.Prev.Value))
                        throw AllocationError.Internal("Invalid previous chunk reference.");
                    var previous = this.chunks[currentChunk.Prev.Value];
                    if (Util.IsOnSamePage(previous.Offset, previous.Size, offset, granularity)
                        && Util.HasGranularityConflict(previous.AllocationType, allocationType))
                    {
                        offset = Util.AlignUp(offset, granularity);
                    }
                }

                var padding = offset - currentChunk.Offset;
                var alignedSize = padding + size;

                if (alignedSize > currentChunk.Size)
                {
                    continue;
                }

                if (currentChunk.Next.HasValue)
                {
                    if (!this.chunks.ContainsKey(currentChunk.Next.Value))
                        throw AllocationError.Internal("Invalid next chunk reference.");
                    var next = this.chunks[currentChunk.Next.Value];
                    if (Util.IsOnSamePage(offset, size, next.Offset, granularity)
                        && Util.HasGranularityConflict(allocationType, next.AllocationType))
                    {
                        continue;
                    }
                }

                if (USE_BEST_FIT)
                {
                    if (!bestFitId.HasValue || currentChunk.Size < bestChunkSize)
                    {
                        bestFitId = currentChunkId;
                        bestAlignedSize = alignedSize;
                        bestOffset = offset;
                        bestChunkSize = currentChunk.Size;
                    }
                }
                else
                {
                    bestFitId = currentChunkId;
                    bestAlignedSize = alignedSize;
                    bestOffset = offset;
                    bestChunkSize = currentChunk.Size;
                    break;
                }
            }

            if (!bestFitId.HasValue)
            {
                throw AllocationError.OutOfMemory;
            }

            var firstFitId = bestFitId.Value;

            ulong chunkId;
            if (bestChunkSize > bestAlignedSize)
            {
                var newChunkId = GetNewChunkId();
                if (!this.chunks.ContainsKey(firstFitId))
                    throw AllocationError.Internal("Chunk ID must be in chunk list.");
                var freeChunk = this.chunks[firstFitId];
                var newChunk = new MemoryChunk
                {
                    ChunkId = newChunkId,
                    Size = bestAlignedSize,
                    Offset = freeChunk.Offset,
                    AllocationType = allocationType,
                    Name = name,
                    Prev = freeChunk.Prev,
                    Next = firstFitId
                };

                freeChunk.Prev = newChunkId;
                freeChunk.Offset += bestAlignedSize;
                freeChunk.Size -= bestAlignedSize;

                if (newChunk.Prev.HasValue)
                {
                    if (!this.chunks.ContainsKey(newChunk.Prev.Value))
                        throw AllocationError.Internal("Invalid previous chunk reference.");
                    var prevChunk = this.chunks[newChunk.Prev.Value];
                    prevChunk.Next = newChunkId;
                }

                this.chunks[newChunkId] = newChunk;
                chunkId = newChunkId;
            }
            else
            {
                if (!this.chunks.ContainsKey(firstFitId))
                    throw AllocationError.Internal("Invalid chunk reference.");
                var chunk = this.chunks[firstFitId];
                chunk.AllocationType = allocationType;
                chunk.Name = name;
                this.RemoveIdFromFreeList(firstFitId);
                chunkId = firstFitId;
            }

            this.allocated += bestAlignedSize;

            return (bestOffset, chunkId);
        }

        public void Free(ulong? chunkId)
        {
            if (!chunkId.HasValue)
            {
                throw AllocationError.Internal("Chunk ID must be a valid value.");
            }

            if (!this.chunks.ContainsKey(chunkId.Value))
                throw AllocationError.Internal("Attempting to free chunk that is not in chunk list.");
            var chunk = this.chunks[chunkId.Value];
            chunk.AllocationType = AllocationType.Free;
            chunk.Name = null;
            this.allocated -= chunk.Size;
            this.freeChunks.TryAdd(chunk.ChunkId, chunk);

            var next_id = chunk.Next;
            var prev_id = chunk.Prev;

            if (next_id.HasValue)
            {
                if (this.chunks[next_id.Value].AllocationType == AllocationType.Free)
                {
                    MergeFreeChunks(chunk.ChunkId, next_id.Value);
                }
            }

            if (prev_id.HasValue)
            {
                if (this.chunks[prev_id.Value].AllocationType == AllocationType.Free)
                {
                    MergeFreeChunks(prev_id.Value, chunk.ChunkId);
                }
            }
        }

        public void RenameAllocation(ulong? chunkId, string name)
        {
            if (!chunkId.HasValue)
            {
                throw AllocationError.Internal("Chunk ID must be a valid value.");
            }

            if (!this.chunks.ContainsKey(chunkId.Value))
                throw AllocationError.Internal("Attempting to rename chunk that is not in chunk list.");
            var chunk = this.chunks[chunkId.Value];
            if (chunk.AllocationType == AllocationType.Free)
            {
                throw AllocationError.Internal("Attempting to rename a freed allocation.");
            }

            chunk.Name = name;
        }

        public void ReportMemoryLeaks(LogLevel logLevel, int memoryTypeIndex, int memoryBlockIndex)
        {
            foreach (var chunk in this.chunks.Values)
            {
                if (chunk.AllocationType == AllocationType.Free)
                {
                    continue;
                }

                var name = chunk.Name ?? "";
                Debug.WriteLine($"Leak detected:\n\tMemory type: {memoryTypeIndex}\n\tMemory block: {memoryBlockIndex}\n\tChunk: {{\n\t\tChunkId: {chunk.ChunkId},\n\t\tSize: {chunk.Size},\n\t\tOffset: {chunk.Offset},\n\t\tAllocationType: {chunk.AllocationType},\n\t\tName: {name}\n\t}}");
            }
        }

        public List<AllocationReport> ReportAllocations()
        {
            return chunks.Values
                .Where(chunk => chunk.AllocationType != AllocationType.Free)
                .Select(chunk => new AllocationReport
                {
                    Name = chunk.Name ?? "<Unnamed FreeList allocation>",
                    Offset = chunk.Offset,
                    Size = chunk.Size,
                })
                .ToList();
        }

        public bool SupportsGeneralAllocations() { return true; }

        public ulong IsAllocated(){ return allocated; }
        #endregion
    }
}
