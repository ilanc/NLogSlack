using NLog;
using NLog.Targets;
using NLog.Targets.Wrappers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lib.Core
{
    /* 
     * Wrapper around NLog.Logger providing:
     *      _LogToConsoleLevel:     Do Console.WriteLine if log level is higher than this threshold
     *      FatalCapped:            Fatals are emailed. Cap the number of FATAL messages that will be emitted from a specific FatalCapped caller - e.g. prevent email flooding when used inside a loop
     *      Error/Warn/InfoConsole: Force output to console as well as NLog
     */
    public sealed class NRLogger
    {
        // The code root on the build machine
        // NOTE: StackFrame and [CallerFilePath] paths are determined at compile time - they don't change at runtime
        static string BuildMachineCodeRoot = @"c:\dev\6\nlogslack\nlogslack\";
        static int BuildMachineCodeRootLen = BuildMachineCodeRoot.Length;
        static string LogFileTargetName = "logfileAsync"; // See: NLog.config

        public enum Level
        {
            Fatal,
            Error,
            Warn,
            Info,
            Trace
        }

        #region Singleton members

        // See thread safe singleton pattern here: http://msdn.microsoft.com/en-us/library/ms998558.aspx
        private static volatile NRLogger _instance = null;     // NB private static member - see below
        private static object _syncRoot = new Object();

        #endregion // Singleton members

        #region Singleton member functions

        // NOTE: Do NOT cache NRLogger.Instance in caller code. Enums is Disposable, which is invoked on Shutdown().
        public static NRLogger Instance
        {
            get
            {
                // NOTE: Not self-initialising - you must call NRLogger.Initialise(..) explicitly
                lock (_syncRoot)
                {
                    if (_instance == null)
                    {
                        throw new Exception("NRLogger.Initialise(..) has NOT been called");
                    }

                    return _instance;
                }
            }
        }

        public static NRLogger Initialise(NRLogger.Level level = NRLogger.Level.Error)
        {
            lock (_syncRoot)
            {
                if (_instance != null)
                {
                    throw new Exception("NRLogger.Initialise(..) has already been called");
                }
                _instance = new NRLogger(level);
                return _instance;
            }
        }

        public static void Shutdown()
        {
            // NOTE: Can have a race condition on Shutdown as follows:
            //  thread 1: calls ICBCodes.Instance and returns _instance to caller
            //  thread 1: suspended before caller uses returned _instance
            //  thread 2: Shutdown() called which forces _instance to null
            // NB cacheing ICBCodes.Instance in caller is very bad for this
            lock (_syncRoot)
            {
                _instance.Flush();
                _instance = null;
            }
        }

        #endregion // Singleton member functions
        
        private Logger Logger { get; set; }
        private Dictionary<string,int> FatalCount = new Dictionary<string,int>(100);
        private NRLogger.Level _LogToConsoleLevel = NRLogger.Level.Error;  // only Errors and Fatal are logged to console

        private NRLogger(NRLogger.Level level)
        {
            PreNLogInit();
            Logger = NLog.LogManager.GetCurrentClassLogger();
            _LogToConsoleLevel = level;
        }

        private static void PreNLogInit()
        {
            // Ensure NLog.Extended is loaded - otherwise NLog fails to read NLog.config properly and disables all logging
            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            var loadedPaths = loadedAssemblies.Select(a => a.Location).ToArray();
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NLog.Extended.dll");
            if (!loadedPaths.Contains(path, StringComparer.InvariantCultureIgnoreCase))
            {
                AppDomain.CurrentDomain.Load(AssemblyName.GetAssemblyName(path));
            }
        }

        public void SetLogToConsoleLevel(NRLogger.Level l)
        {
            _LogToConsoleLevel = l;
        }

        #region Util

        public void Flush()
        {
            //LogManager.Flush(ex => { }, TimeSpan.FromSeconds(2));
            LogManager.Flush();
        }

        public static string GetLogFileName()
        {
            string fileName = null;

            if (LogManager.Configuration != null && LogManager.Configuration.ConfiguredNamedTargets.Count != 0)
            {
                Target target = LogManager.Configuration.FindTargetByName(LogFileTargetName);
                if (target == null)
                {
                    throw new Exception("Could not find target named: " + LogFileTargetName);
                }

                FileTarget fileTarget = null;
                WrapperTargetBase wrapperTarget = target as WrapperTargetBase;

                // Unwrap the target if necessary.
                if (wrapperTarget == null)
                {
                    fileTarget = target as FileTarget;
                }
                else
                {
                    fileTarget = wrapperTarget.WrappedTarget as FileTarget;
                }

                if (fileTarget == null)
                {
                    throw new Exception("Could not get a FileTarget from " + target.GetType());
                }

                var logEventInfo = new LogEventInfo { TimeStamp = DateTime.Now };
                fileName = fileTarget.FileName.Render(logEventInfo);
            }
            else
            {
                throw new Exception("LogManager contains no Configuration or there are no named targets");
            }

            if (!File.Exists(fileName))
            {
                LogManager.Flush();
                if (!File.Exists(fileName))
                {
                    throw new Exception("Logfile " + fileName + " does not exist even after flush");
                }
            }

            return fileName;
        }

        /*
        private void InitConsoleTargetAndRule()
        {
            Target target = LogManager.Configuration.AllTargets.FirstOrDefault(x => x.Name == "console");
            LoggingRule rule = new LoggingRule("*", LogLevel.Trace, target);
            LogManager.Configuration.LoggingRules.Add(rule);
        }
         */

        private void LogToConsole(NRLogger.Level level, string message, object[] args = null)
        {
            if ((int)level <= (int)_LogToConsoleLevel)
            {
                var m = (args == null) ? message : string.Format(message, args);
                Console.WriteLine(level.ToString() + " :" + m);
            }
        }

        private void Log(LogLevel level, string message, string caller, object[] arguments = null, int? portfolioId = null)
        {
            var eventInfo = new LogEventInfo(level, Logger.Name, message);
            eventInfo.Properties["caller"] = caller;
            if (portfolioId.HasValue)
            {
                eventInfo.Properties["PortfolioId"] = portfolioId.Value;
            }
            if (arguments != null)
            {
                eventInfo.Parameters = arguments;
            }
            Logger.Log(eventInfo);
        }

        #endregion

        #region Fatal

        /*
         * No need for duplicates - the versions with the params object[] args below will be called instead
         * 
        public void Fatal(string message,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var caller = string.Format("{0}({1})", file.Substring(BuildMachineCodeRootLen), line);
            LogToConsole(NRLogger.Level.Fatal, message);
            Log(LogLevel.Fatal, message, caller);
        }

        public void Fatal(int portfolioId, string message,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            var caller = string.Format("{0}({1})", file.Substring(BuildMachineCodeRootLen), line);
            LogToConsole(NRLogger.Level.Fatal, message);
            Log(LogLevel.Fatal, message, caller, null, portfolioId);
        }
        */

        public void Fatal(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Fatal, message, args);
            Log(LogLevel.Fatal, message, caller, args);
        }

        public void Fatal(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Fatal, message, args);
            Log(LogLevel.Fatal, message, caller, args, portfolioId);
        }

        public void FatalCapped(int cap, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            if (!FatalCount.ContainsKey(caller))
            {
                FatalCount[caller] = 0;
            }

            if (FatalCount[caller]++ < cap)
            {
                LogToConsole(NRLogger.Level.Fatal, message, args);
                Log(LogLevel.Fatal, message, caller);
            }
            else
            {
                LogToConsole(NRLogger.Level.Error, message, args);
                Log(LogLevel.Error, message, caller);
            }
        }

        public void FatalCapped(int cap, int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            if (!FatalCount.ContainsKey(caller))
            {
                FatalCount[caller] = 0;
            }

            if (FatalCount[caller]++ < cap)
            {
                LogToConsole(NRLogger.Level.Fatal, message, args);
                Log(LogLevel.Fatal, message, caller, null, portfolioId);
            }
            else
            {
                LogToConsole(NRLogger.Level.Error, message, args);
                Log(LogLevel.Error, message, caller, null, portfolioId);
            }
        }

        #endregion

        #region Error

        public void Error(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Error, message, args);
            Log(LogLevel.Error, message, caller, args);
        }

        public void Error(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Error, message, args);
            Log(LogLevel.Error, message, caller, args, portfolioId);
        }

        public void ErrorConsole(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Error.ToString() + " :" + m);
            Log(LogLevel.Error, message, caller, args);
        }

        public void ErrorConsole(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Error.ToString() + " :" + m + ":" + portfolioId);
            Log(LogLevel.Error, message, caller, args, portfolioId);
        }

        #endregion

        #region Warn

        public void Warn(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Warn, message, args);
            Log(LogLevel.Warn, message, caller, args);
        }

        public void Warn(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Warn, message, args);
            Log(LogLevel.Warn, message, caller, args, portfolioId);
        }

        public void WarnConsole(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Warn.ToString() + " :" + m);
            Log(LogLevel.Warn, message, caller, args);
        }

        public void WarnConsole(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Warn.ToString() + " :" + m + ":" + portfolioId);
            Log(LogLevel.Warn, message, caller, args, portfolioId);
        }

        #endregion

        #region Info

        public void Info(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Info, message, args);
            Log(LogLevel.Info, message, caller, args);
        }

        public void Info(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Info, message, args);
            Log(LogLevel.Info, message, caller, args, portfolioId);
        }

        public void InfoConsole(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Info.ToString() + " :" + m);
            Log(LogLevel.Info, message, caller, args);
        }

        public void InfoConsole(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Info.ToString() + " :" + m + ":" + portfolioId);
            Log(LogLevel.Info, message, caller, args, portfolioId);
        }

        #endregion

        #region Trace

        public void Trace(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Trace, message, args);
            Log(LogLevel.Trace, message, caller, args);
        }

        public void Trace(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            LogToConsole(NRLogger.Level.Trace, message, args);
            Log(LogLevel.Trace, message, caller, args, portfolioId);
        }

        public void TraceConsole(string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Trace.ToString() + " :" + m);
            Log(LogLevel.Trace, message, caller, args);
        }

        public void TraceConsole(int portfolioId, string message, params object[] args)
        {
            StackFrame stackFrame = new StackFrame(1, true);
            string file = stackFrame.GetFileName().Substring(BuildMachineCodeRootLen);
            int line = stackFrame.GetFileLineNumber();
            var caller = string.Format("{0}({1})", file, line);

            var m = (args == null) ? message : string.Format(message, args);
            Console.WriteLine(NRLogger.Level.Trace.ToString() + " :" + m + ":" + portfolioId);
            Log(LogLevel.Trace, message, caller, args, portfolioId);
        }

        #endregion
    }
}
