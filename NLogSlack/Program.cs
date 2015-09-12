using Lib.Core;

namespace NLogSlack
{
    class Program
    {
        static void Main(string[] args)
        {
            Test4();
        }

        #region Test4

        private static void Test4()
        {
            var l = NRLogger.Initialise();

            l.Info("Testing NRLogger...");
            l.Info("Testing NRLogger...{0},{1},{2}", "xxx", 10, 99);
            l.InfoConsole("DUPLICATED if nlog.config console rule used: {0},{1},{2}", "xxx", 10, 99);
            l.InfoConsole(1000, "DUPLICATED if nlog.config console rule used: {0},{1},{2}", "xxx", 10, 99);

            NRLogger.Shutdown();
        }

        #endregion
    }
}
