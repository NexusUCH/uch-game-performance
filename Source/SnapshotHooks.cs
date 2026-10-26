using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SevenZip.Compression.LZMA;
using UnityEngine;
using UnityEngine.Networking;

namespace UCHGamePerformance
{
    // 2.3.1: when a level starts, the host LZMA-compresses the level XML on the main thread (~1 s on a full level) only
    // to send it to remote clients. The host's own copies of those RPCs return at once because it has authority, and the
    // host loads the XML string directly. In a local game nobody receives the bytes, so they are passed on uncompressed.
    // Online lobbies, any remote player and any remote connection keep vanilla compression: remote clients always
    // decompress (QuickSaver.LoadCompressedSnapshotThreaded), so raw bytes must never reach them.
    internal static class SnapshotHooks
    {
        internal static readonly Feature Skip = new Feature("Loading/SkipLocalSnapshotCompress");
        private static long skipped, compressed;

        internal static void Install()
        {
            Skip.Patch(Reflect.DeclaredMethod(typeof(GameControl), "CompressAndSendSnapshotBytes"), typeof(SnapshotHooks), null, null, "UseLocalCheck", null);
        }

        internal static IEnumerable<CodeInstruction> UseLocalCheck(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            MethodInfo compress = typeof(SevenZipHelper).GetMethod("Compress", new Type[] { typeof(byte[]) });
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Call || !Equals(instruction.operand, compress)) continue;
                instruction.operand = Reflect.DeclaredMethod(typeof(SnapshotHooks), "CompressUnlessLocal");
                count++;
            }
            Guards.Count(count, 1, "snapshot compression");
            return code;
        }

        private static byte[] CompressUnlessLocal(byte[] bytes)
        {
            if (Skip.Active)
            {
                try { if (OnlyLocalPlayers()) { skipped++; return bytes; } }
                catch (Exception e) { Skip.Fail(e); }
            }
            compressed++;
            return SevenZipHelper.Compress(bytes);
        }

        private static bool OnlyLocalPlayers()
        {
            LobbyManager lobby = LobbyManager.instance;
            if (lobby == null || !lobby.IsHost || lobby.IsInOnlineGame || lobby.PlayerTracker == null || !NetworkServer.active) return false;
            foreach (uint id in lobby.PlayerTracker.GetAllGameNetIDs())
            {
                GameObject player = ClientScene.FindLocalObject(new NetworkInstanceId(id));
                GamePlayer gamePlayer = player == null ? null : player.GetComponent<GamePlayer>();
                if (gamePlayer == null || !gamePlayer.IsLocalPlayer) return false;
            }
            foreach (NetworkConnection connection in NetworkServer.connections)
                if (connection != null && connection.hostId >= 0) return false;
            return true;
        }

        internal static string Status() { return "skipped=" + skipped + " compressed=" + compressed; }
    }
}
