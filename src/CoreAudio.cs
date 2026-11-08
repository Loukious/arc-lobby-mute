using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ArcLobbyMute
{
    internal sealed class AudioSession : IDisposable
    {
        private IAudioSessionControl2 control;
        private ISimpleAudioVolume volume;
        public readonly uint ProcessId;
        public readonly string Key;

        public AudioSession(IAudioSessionControl2 control, uint processId, string key)
        {
            this.control = control;
            volume = (ISimpleAudioVolume)control;
            ProcessId = processId;
            Key = key;
        }

        public bool Muted
        {
            get { bool value; Marshal.ThrowExceptionForHR(volume.GetMute(out value)); return value; }
            set
            {
                Guid context = new Guid("E77D9864-2C3C-43C5-B497-B032251478DC");
                Marshal.ThrowExceptionForHR(volume.SetMute(value, ref context));
            }
        }

        public float Level
        {
            get { float value; Marshal.ThrowExceptionForHR(volume.GetMasterVolume(out value)); return value; }
        }

        public void Dispose()
        {
            volume = null; // Both interfaces belong to the same RCW: release exactly once.
            CoreAudio.Release(control);
            control = null;
        }
    }

    internal sealed class SessionScan : IDisposable
    {
        public readonly List<AudioSession> Sessions = new List<AudioSession>();
        public bool Complete = true;
        public string Error;
        public void Dispose() { foreach (AudioSession session in Sessions) session.Dispose(); }
    }

    internal static class CoreAudio
    {
        internal static void Release(object instance)
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }

        public static SessionScan Scan(HashSet<uint> processIds)
        {
            var result = new SessionScan();
            if (processIds.Count == 0) return result;
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection devices = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // All active playback devices, including a non-default headset selected by the game.
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out devices));
                uint count;
                Marshal.ThrowExceptionForHR(devices.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IAudioSessionManager2 manager = null;
                    IAudioSessionEnumerator sessions = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(devices.Item(i, out device));
                        string deviceId;
                        Marshal.ThrowExceptionForHR(device.GetId(out deviceId));
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        object activated;
                        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out activated));
                        manager = (IAudioSessionManager2)activated;
                        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                        int sessionCount;
                        Marshal.ThrowExceptionForHR(sessions.GetCount(out sessionCount));
                        for (int s = 0; s < sessionCount; s++)
                        {
                            IAudioSessionControl2 control = null;
                            try
                            {
                                Marshal.ThrowExceptionForHR(sessions.GetSession(s, out control));
                                uint pid;
                                Marshal.ThrowExceptionForHR(control.GetProcessId(out pid));
                                int state;
                                Marshal.ThrowExceptionForHR(control.GetState(out state));
                                if (state == 2 || !processIds.Contains(pid)) continue;
                                string id;
                                Marshal.ThrowExceptionForHR(control.GetSessionInstanceIdentifier(out id));
                                result.Sessions.Add(new AudioSession(control, pid, deviceId + "|" + id));
                                control = null; // Ownership transferred to AudioSession.
                            }
                            catch (COMException ex) { result.Complete = false; result.Error = ex.Message; }
                            finally { Release(control); }
                        }
                    }
                    catch (COMException ex) { result.Complete = false; result.Error = ex.Message; }
                    finally { Release(sessions); Release(manager); Release(device); }
                }
            }
            catch (COMException ex) { result.Complete = false; result.Error = ex.Message; }
            finally { Release(devices); Release(enumerator); }
            return result;
        }
    }

    internal sealed class AudioController
    {
        private sealed class OriginalState
        {
            public uint ProcessId;
            public bool Muted;
        }
        private readonly Dictionary<string, OriginalState> originals = new Dictionary<string, OriginalState>();
        public int SessionCount { get; private set; }
        public string LastError { get; private set; }

        public void Apply(HashSet<uint> processIds, bool mute)
        {
            LastError = null;
            using (SessionScan scan = CoreAudio.Scan(processIds))
            {
                SessionCount = scan.Sessions.Count;
                LastError = scan.Error;
                var seen = new HashSet<string>();
                foreach (AudioSession session in scan.Sessions)
                {
                    seen.Add(session.Key);
                    try
                    {
                        bool current = session.Muted;
                        if (!originals.ContainsKey(session.Key))
                            originals.Add(session.Key, new OriginalState { ProcessId = session.ProcessId, Muted = current });
                        if (current != mute) session.Muted = mute;
                    }
                    catch (COMException ex) { LastError = ex.Message; }
                }
                // Only prune sessions that actually disappeared after a complete scan.
                if (scan.Complete)
                    foreach (string key in new List<string>(originals.Keys))
                        if (!seen.Contains(key)) originals.Remove(key);
            }
        }

        public bool Restore()
        {
            LastError = null;
            if (originals.Count == 0) { SessionCount = 0; return true; }
            var processIds = new HashSet<uint>();
            foreach (OriginalState original in originals.Values) processIds.Add(original.ProcessId);
            using (SessionScan scan = CoreAudio.Scan(processIds))
            {
                LastError = scan.Error;
                var seen = new HashSet<string>();
                foreach (AudioSession session in scan.Sessions)
                {
                    seen.Add(session.Key);
                    OriginalState original;
                    if (!originals.TryGetValue(session.Key, out original)) continue;
                    try
                    {
                        if (session.Muted != original.Muted) session.Muted = original.Muted;
                        originals.Remove(session.Key);
                    }
                    catch (COMException ex) { LastError = ex.Message; }
                }
                if (scan.Complete)
                    foreach (string key in new List<string>(originals.Keys))
                        if (!seen.Contains(key)) originals.Remove(key);
            }
            SessionCount = 0;
            return originals.Count == 0;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // Inherited IAudioSessionControl slots are flattened in their native vtable order.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
