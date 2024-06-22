namespace GPUAllocator.NET
{
    public class AllocationError : Exception
    {
        public AllocationError(AllocationErrorType type, string message) : base(message)
        {
            ErrorType = type;
        }

        public enum AllocationErrorType
        {
            OutOfMemory,
            FailedToMap,
            NoCompatibleMemoryTypeFound,
            InvalidAllocationCreateDesc, 
            InvalidAllocatorCreateDesc, 
            Internal, 
            BarrierLayoutNeedsDevice10, 
            CastableFormatsRequiresEnhancedBarriers, 
            CastableFormatsRequiresAtLeastDevice12
        }

        public AllocationErrorType ErrorType;

        public static AllocationError OutOfMemory = new AllocationError(AllocationErrorType.OutOfMemory, "Out of memory");
        public static AllocationError FailedToMap(string s) => new AllocationError(AllocationErrorType.FailedToMap, s);
        public static AllocationError NoCompatibleMemoryTypeFound = new AllocationError(AllocationErrorType.NoCompatibleMemoryTypeFound, "No compatible memory type available");
        public static AllocationError InvalidAllocationCreateDesc = new AllocationError(AllocationErrorType.InvalidAllocationCreateDesc, "Invalid AllocationCreateDesc");
        public static AllocationError InvalidAllocatorCreateDesc(string s) => new AllocationError(AllocationErrorType.InvalidAllocatorCreateDesc, s);
        public static AllocationError Internal(string s) => new AllocationError(AllocationErrorType.Internal, s);
        public static AllocationError BarrierLayoutNeedsDevice10 = new AllocationError(AllocationErrorType.BarrierLayoutNeedsDevice10, "Initial `BARRIER_LAYOUT` needs at least `Device10`");
        public static AllocationError CastableFormatsRequiresEnhancedBarriers = new AllocationError(AllocationErrorType.CastableFormatsRequiresEnhancedBarriers, "Castable formats require enhanced barriers");
        public static AllocationError CastableFormatsRequiresAtLeastDevice12 = new AllocationError(AllocationErrorType.CastableFormatsRequiresAtLeastDevice12, "Castable formats require at least `Device12`");
    }
}
