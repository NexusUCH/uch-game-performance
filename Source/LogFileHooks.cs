using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.1: the game's LogCatcher appends every log line to Logs/uch-log-file-1.log with File.AppendAllText, i.e. it
    // opens, writes and closes that file once per line on the logging thread (~0.6 ms per line, measured live). A level
    // start logs one "Placing ..." line per block, so a full level pays ~2 s for this alone, and every log line during
    // play is a small hitch. The game formats exactly the same text; only the write is buffered: lines keep their order
    // and are appended to the same file by a timer every 250 ms, when the buffer grows large, and when the game quits.
    internal static class LogFileHooks
    {
        internal static readonly Feature Buffer = new Feature("CPU/BufferedLogFile");
        private const int MaxPendingChars = 1 << 20;
        private static readonly object sync = new object();
        private static readonly object writeSync = new object();
        private static StringBuilder pending = new StringBuilder();
        private static string pendingPath;
        private static Timer timer;
        private static long lines, writes, failures;

        internal static void Install()
        {
            Buffer.Restore = delegate {
                Application.quitting -= Flush;
                Timer old = timer; timer = null;
                if (old != null) old.Dispose();
                Flush();
            };
            Buffer.Patch(Reflect.DeclaredMethod(typeof(LogCatcher), "LogMessageReceived"), typeof(LogFileHooks), null, null, "Redirect", null);
            Application.quitting += Flush;
            timer = new Timer(Tick, null, 250, 250);
        }

        internal static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            MethodInfo append = typeof(File).GetMethod("AppendAllText", new Type[] { typeof(string), typeof(string) });
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Call || !Equals(instruction.operand, append)) continue;
                instruction.operand = Reflect.DeclaredMethod(typeof(LogFileHooks), "Append");
                count++;
            }
            Guards.Count(count, 1, "log file append");
            return code;
        }

        // Runs on whichever thread logged (logMessageReceivedThreaded); call order is the file order.
        private static void Append(string path, string contents)
        {
            if (!Buffer.Active || timer == null) { File.AppendAllText(path, contents); return; }
            bool otherFile = false;
            lock (sync)
            {
                if (pendingPath != null && pendingPath != path && pending.Length != 0) otherFile = true;
                else
                {
                    pendingPath = path;
                    pending.Append(contents);
                    lines++;
                    if (pending.Length < MaxPendingChars) return;
                }
            }
            Flush();
            // A different target file: earlier lines are written first, then this one is buffered.
            if (otherFile) Append(path, contents);
        }

        private static void Tick(object state)
        {
            // Timer threads must never throw.
            try { Flush(); } catch (Exception) { Interlocked.Increment(ref failures); }
        }

        internal static void Flush()
        {
            lock (writeSync)
            {
                string path, text;
                lock (sync)
                {
                    if (pending.Length == 0) { pendingPath = null; return; }
                    path = pendingPath; text = pending.ToString();
                    pending = new StringBuilder(); pendingPath = null;
                }
                try { File.AppendAllText(path, text); writes++; }
                catch (Exception)
                {
                    // Keep the lines (in front, so order is unchanged) and retry on the next tick, within a size cap.
                    failures++;
                    lock (sync)
                    {
                        if (pendingPath == null || pendingPath == path)
                        {
                            if (text.Length + pending.Length <= 4 * MaxPendingChars) pending.Insert(0, text);
                            pendingPath = path;
                        }
                    }
                }
            }
        }

        internal static string Status() { return "lines=" + lines + " writes=" + writes + " failures=" + failures; }
    }
}
