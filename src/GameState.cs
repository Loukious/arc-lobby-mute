using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ArcLobbyMute
{
    internal enum GameState { Offline, Waiting, Lobby, Match, Unknown }

    internal sealed class GameSnapshot
    {
        public readonly HashSet<uint> ProcessIds = new HashSet<uint>();
        public bool ConnectionPresent;
        public bool ConnectionFresh;
        public bool ReadFailed;
    }

    internal sealed class GameMonitor
    {
        internal static readonly string ConnectionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Embark\Pioneer\Connectinfo.json");

        public GameSnapshot Read()
        {
            var snapshot = new GameSnapshot();
            DateTime earliestStart = DateTime.MaxValue;
            foreach (Process process in Process.GetProcessesByName("PioneerGame"))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        snapshot.ProcessIds.Add((uint)process.Id);
                        DateTime start = process.StartTime.ToUniversalTime();
                        if (start < earliestStart) earliestStart = start;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { snapshot.ReadFailed = true; }
                }
            }
            if (snapshot.ProcessIds.Count == 0) return snapshot;
            try
            {
                // GetAttributes distinguishes a missing file from access/IO errors.
                File.GetAttributes(ConnectionPath);
                snapshot.ConnectionPresent = true;
                snapshot.ConnectionFresh = File.GetLastWriteTimeUtc(ConnectionPath) >= earliestStart.AddSeconds(-2);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { snapshot.ReadFailed = true; }
            catch (UnauthorizedAccessException) { snapshot.ReadFailed = true; }
            return snapshot;
        }
    }

    internal sealed class StateDetector
    {
        private TimeSpan? missingSince;
        private HashSet<uint> previousProcesses = new HashSet<uint>();
        private GameState previous = GameState.Offline;

        public GameState Update(GameSnapshot snapshot, TimeSpan now)
        {
            bool newSession = !previousProcesses.SetEquals(snapshot.ProcessIds);
            previousProcesses = new HashSet<uint>(snapshot.ProcessIds);
            if (snapshot.ReadFailed)
            {
                missingSince = null;
                return previous = GameState.Unknown;
            }
            if (snapshot.ProcessIds.Count == 0)
            {
                missingSince = null;
                return previous = GameState.Offline;
            }
            if (newSession)
            {
                missingSince = null;
                previous = GameState.Waiting;
            }
            if (snapshot.ConnectionPresent && snapshot.ConnectionFresh)
            {
                missingSince = null;
                return previous = GameState.Match;
            }
            if (!missingSince.HasValue) missingSince = now;
            // Avoid muting during short file replacement gaps. Unmute immediately on creation.
            if (now - missingSince.Value < TimeSpan.FromSeconds(2))
                return previous == GameState.Match ? GameState.Match : GameState.Waiting;
            return previous = GameState.Lobby;
        }
    }
}
