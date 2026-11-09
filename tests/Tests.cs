using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ArcLobbyMute
{
    internal static class Tests
    {
        private static int assertions;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            assertions++;
            Console.WriteLine("PASS: " + message);
        }

        private static GameSnapshot Snapshot(bool running, bool present, bool fresh, bool matchSocket = true)
        {
            var result = new GameSnapshot { ConnectionPresent = present, ConnectionFresh = fresh, MatchSocketPresent = matchSocket };
            if (running) result.ProcessIds.Add(123);
            return result;
        }

        private static GameState Step(StateDetector detector, GameSnapshot snapshot, double seconds)
        {
            return detector.Update(snapshot, TimeSpan.FromSeconds(seconds));
        }

        private static void StateTests()
        {
            var detector = new StateDetector();
            Check(Step(detector, Snapshot(false, true, true), 0) == GameState.Offline, "Stale file cannot imply a running game");
            Check(Step(detector, Snapshot(true, false, false), 1) == GameState.Waiting, "Startup waits before muting");
            Check(Step(detector, Snapshot(true, false, false), 2) == GameState.Waiting, "One missing-file sample is insufficient");
            Check(Step(detector, Snapshot(true, false, false), 3) == GameState.Lobby, "Stable file absence detects lobby");
            Check(Step(detector, Snapshot(true, true, true), 3.1) == GameState.Match, "Entering a match unmutes immediately");
            Check(Step(detector, Snapshot(true, false, false), 4) == GameState.Match, "Brief file removal does not mute a match");
            Check(Step(detector, Snapshot(true, true, true), 5) == GameState.Match, "File replacement cancels lobby countdown");
            Check(Step(detector, Snapshot(true, false, false), 6) == GameState.Match, "A later removal starts a new countdown");
            Check(Step(detector, Snapshot(true, false, false), 8) == GameState.Lobby, "Returning to lobby mutes after two seconds");
            Check(Step(detector, Snapshot(false, false, false), 9) == GameState.Offline, "Game exit clears match state");
            Check(Step(detector, Snapshot(true, true, false), 10) == GameState.Waiting, "Old-session file is ignored after restart");
            Check(Step(detector, Snapshot(true, true, false), 12) == GameState.Lobby, "Old-session file does not cause false match detection");
            var failed = Snapshot(true, false, false);
            failed.ReadFailed = true;
            Check(Step(detector, failed, 13) == GameState.Unknown, "Unreadable state is unknown rather than lobby");
            Check(Step(detector, Snapshot(true, false, false), 14) == GameState.Waiting, "Read recovery gets a new grace period");
            Check(Step(detector, Snapshot(true, true, true), 15) == GameState.Match, "Read recovery can detect match immediately");
            var restarted = Snapshot(true, false, false);
            restarted.ProcessIds.Clear(); restarted.ProcessIds.Add(456);
            Check(Step(detector, restarted, 16) == GameState.Waiting, "Process replacement cannot inherit previous match state");

            detector = new StateDetector();
            var retainedFile = Snapshot(true, true, true, false);
            Check(Step(detector, retainedFile, 0) == GameState.Waiting, "Starting in lobby with a retained current-launch file waits before muting");
            Check(Step(detector, retainedFile, 2) == GameState.Lobby, "A retained current-launch file without its socket is lobby");
            Check(Step(detector, Snapshot(true, true, true), 3) == GameState.Match, "A new match socket unmutes immediately");
            Check(Step(detector, Snapshot(true, true, true), 3600) == GameState.Match, "An open match socket does not expire during a long match");
            Check(Step(detector, retainedFile, 3601) == GameState.Match, "A brief socket gap preserves match audio");
            Check(Step(detector, Snapshot(true, true, true), 3602) == GameState.Match, "Socket recovery cancels the lobby countdown");
            Check(Step(detector, retainedFile, 3603) == GameState.Match, "Socket closure starts a new lobby countdown");
            Check(Step(detector, retainedFile, 3605) == GameState.Lobby, "Returning to lobby mutes even when Connectinfo.json remains");
            failed = Snapshot(true, true, true);
            failed.ReadFailed = true;
            Check(Step(detector, failed, 3606) == GameState.Unknown, "An unreadable socket table cannot imply lobby or match");
            Check(Step(detector, retainedFile, 3607) == GameState.Waiting, "Socket scan recovery gets a fresh grace period");
            Check(Step(detector, Snapshot(true, true, true), 3608) == GameState.Match, "The next match unmutes after a retained-file lobby");
        }

        private static void SocketMatchingTests()
        {
            var ids = new HashSet<uint> { 123 };
            DateTime written = DateTime.UtcNow.AddHours(-2);
            var match = new UdpEndpoint { ProcessId = 123, LocalPort = 57185, Wildcard = true, CreatedUtc = written.AddMilliseconds(12) };
            var endpoints = new List<UdpEndpoint> { match };
            Check(UdpConnections.HasMatchSocket(endpoints, ids, written), "A long-running socket still matches its original connection timestamp");
            Check(!UdpConnections.HasMatchSocket(endpoints, ids, written.AddMinutes(10)), "An earlier match socket cannot match a rewritten connection file");
            Check(!UdpConnections.HasMatchSocket(endpoints, new HashSet<uint> { 456 }, written), "A different process's socket cannot indicate a match");
            endpoints.Add(new UdpEndpoint { ProcessId = 123, LocalPort = 54562, Wildcard = true, CreatedUtc = written.AddMinutes(-20) });
            endpoints.Add(new UdpEndpoint { ProcessId = 123, LocalPort = 52618, Wildcard = false, CreatedUtc = written });
            endpoints.Remove(match);
            Check(!UdpConnections.HasMatchSocket(endpoints, ids, written), "Lobby service sockets and interface-bound voice sockets do not preserve a match");
            endpoints.Add(new UdpEndpoint { ProcessId = 123, LocalPort = 57185, Wildcard = true, CreatedUtc = written.AddMinutes(5) });
            Check(!UdpConnections.HasMatchSocket(endpoints, ids, written), "Reusing a closed socket's port later cannot revive a retained connection file");
            endpoints.Add(new UdpEndpoint { ProcessId = 123, Wildcard = true, CreatedUtc = written.AddMilliseconds(-500) });
            Check(UdpConnections.HasMatchSocket(endpoints, ids, written), "A socket opened just before the file write is recognized");
        }

        private static UdpEndpoint FindSocket(List<UdpEndpoint> endpoints, int port, DateTime? created)
        {
            foreach (UdpEndpoint endpoint in endpoints)
                if (endpoint.LocalPort == port && endpoint.Wildcard && (!created.HasValue || endpoint.CreatedUtc == created.Value))
                    return endpoint;
            return null;
        }

        private static void SocketIntegrationTest(AddressFamily family, IPAddress address, string label)
        {
            var ids = new HashSet<uint> { (uint)Process.GetCurrentProcess().Id };
            int port;
            UdpEndpoint bound;
            DateTime before = DateTime.UtcNow;
            using (var socket = new UdpClient(family))
            {
                socket.Client.Bind(new IPEndPoint(address, 0));
                port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
                bound = FindSocket(UdpConnections.Read(ids), port, null);
                Check(bound != null, label + " table discovers a real local socket with its owner and port");
                Check(bound.CreatedUtc >= before.AddSeconds(-1) && bound.CreatedUtc <= DateTime.UtcNow.AddSeconds(1),
                    label + " table exposes the socket's real creation timestamp");
                Check(UdpConnections.HasMatchSocket(new[] { bound }, ids, before), label + " socket correlates with a connection created at the same time");
                Check(UdpConnections.Read(new HashSet<uint> { uint.MaxValue }).Count == 0, label + " scan isolates the requested process IDs");
            }
            Check(FindSocket(UdpConnections.Read(ids), port, bound.CreatedUtc) == null, label + " socket disappears from the table after it closes");
        }

        private static MemoryStream SilentWave()
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            int bytes = 44100 * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100);
            writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            writer.Write(new byte[bytes]); writer.Flush(); stream.Position = 0;
            return stream;
        }

        private static bool TestMuted(HashSet<uint> ids)
        {
            using (SessionScan scan = CoreAudio.Scan(ids))
            {
                if (!scan.Complete || scan.Sessions.Count == 0)
                    throw new Exception("Test session unavailable: " + scan.Error);
                return scan.Sessions[0].Muted;
            }
        }

        private static void AudioTests()
        {
            var ids = new HashSet<uint> { (uint)Process.GetCurrentProcess().Id };
            var controller = new AudioController();
            using (MemoryStream wave = SilentWave())
            using (var player = new SoundPlayer(wave))
            {
                player.PlayLooping();
                bool found = false;
                for (int i = 0; i < 30; i++)
                {
                    using (SessionScan scan = CoreAudio.Scan(ids))
                        found = scan.Complete && scan.Sessions.Count > 0;
                    if (found) break;
                    Thread.Sleep(100);
                }
                Check(found, "Core Audio discovers the silent test process on a playback device");
                bool originalMute = TestMuted(ids);
                float originalLevel;
                using (SessionScan scan = CoreAudio.Scan(ids)) originalLevel = scan.Sessions[0].Level;
                try
                {
                    using (SessionScan scan = CoreAudio.Scan(ids))
                        foreach (AudioSession session in scan.Sessions) session.Muted = false;
                    controller.Apply(ids, true);
                    Check(controller.LastError == null && TestMuted(ids), "Lobby action mutes the selected Windows audio session");
                    controller.Apply(ids, false);
                    Check(!TestMuted(ids), "Match action unmutes the selected Windows audio session");
                    using (SessionScan scan = CoreAudio.Scan(ids))
                        Check(Math.Abs(scan.Sessions[0].Level - originalLevel) < 0.00001, "Mute toggling preserves the volume slider");
                    controller.Apply(ids, true);
                    Check(controller.Restore() && !TestMuted(ids), "Pause/exit restores an originally unmuted session");
                    using (SessionScan scan = CoreAudio.Scan(ids))
                        foreach (AudioSession session in scan.Sessions) session.Muted = true;
                    controller.Apply(ids, false);
                    Check(!TestMuted(ids), "Automation can unmute an originally muted session");
                    Check(controller.Restore() && TestMuted(ids), "Pause/exit restores an originally muted session");
                    controller.Apply(new HashSet<uint> { uint.MaxValue }, false);
                    Check(controller.SessionCount == 0 && TestMuted(ids), "Unrelated process IDs leave this session untouched");
                }
                finally
                {
                    controller.Restore();
                    using (SessionScan scan = CoreAudio.Scan(ids))
                        foreach (AudioSession session in scan.Sessions) session.Muted = originalMute;
                    player.Stop();
                }
            }
        }

        private static void Inspect()
        {
            GameSnapshot snapshot = new GameMonitor().Read();
            Console.WriteLine("Game running: " + (snapshot.ProcessIds.Count > 0));
            Console.WriteLine("Connection file present: " + snapshot.ConnectionPresent);
            Console.WriteLine("Connection belongs to current launch: " + snapshot.ConnectionFresh);
            Console.WriteLine("Matching game socket present: " + snapshot.MatchSocketPresent);
            Console.WriteLine("Read failed: " + snapshot.ReadFailed);
            using (SessionScan scan = CoreAudio.Scan(snapshot.ProcessIds))
            {
                Console.WriteLine("Scan complete: " + scan.Complete);
                Console.WriteLine("Game audio sessions: " + scan.Sessions.Count);
                foreach (AudioSession session in scan.Sessions)
                    Console.WriteLine("PID=" + session.ProcessId + " Muted=" + session.Muted + " Volume=" + session.Level.ToString("P0"));
                if (scan.Error != null) Console.WriteLine("Audio error: " + scan.Error);
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (Array.IndexOf(args, "--inspect") >= 0) { Inspect(); return 0; }
                StateTests();
                SocketMatchingTests();
                SocketIntegrationTest(AddressFamily.InterNetwork, IPAddress.Any, "IPv4");
                if (Socket.OSSupportsIPv6) SocketIntegrationTest(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, "IPv6");
                if (Array.IndexOf(args, "--state-only") < 0) AudioTests();
                Console.WriteLine("All " + assertions + " checks passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
