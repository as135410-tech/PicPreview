namespace QuickLooker.Core;

internal static class SqliteBootstrap
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        SQLitePCL.Batteries_V2.Init();
    }
}
