using System.Threading;

namespace Network;

internal static class SessionIdGenerator
{
    private static long sessionCounter = 0;

    public static long Next()
    {
        return Interlocked.Increment(ref sessionCounter);
    }
}
