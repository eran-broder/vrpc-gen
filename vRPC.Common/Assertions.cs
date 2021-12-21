using System.Runtime.CompilerServices;

namespace vRPC.Common
{
    public static class Assertions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Assert(bool condition, string msg = "")
        {
            if (!condition)
                throw new Exception(msg);
        }

        public static void NotNull<T>(T? value, string msg = "")
        {
            Assert(value != null);
        }
    }
}