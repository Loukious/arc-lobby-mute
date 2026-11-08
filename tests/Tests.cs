using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Media;
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

        private static GameSnapshot Snapshot(bool running, bool present, bool fresh)
        {
            var result = new GameSnapshot { ConnectionPresent = present, ConnectionFresh = fresh };
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
                if (Array.IndexOf(args, "--state-only") < 0) AudioTests();
                Console.WriteLine("All " + assertions + " checks passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
