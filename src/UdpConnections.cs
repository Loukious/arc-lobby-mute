using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ArcLobbyMute
{
    internal sealed class UdpEndpoint
    {
        public uint ProcessId;
        public int LocalPort;
        public DateTime CreatedUtc;
        public bool Wildcard;
    }

    internal static class UdpConnections
    {
        // Connectinfo.json is written as the game's match socket is opened. Compare
        // these two creation times, NOT the age of the file: matches can be long.
        internal static bool HasMatchSocket(IEnumerable<UdpEndpoint> endpoints,
            HashSet<uint> processIds, DateTime connectionWrittenUtc)
        {
            foreach (UdpEndpoint endpoint in endpoints)
            {
                if (processIds.Contains(endpoint.ProcessId) && endpoint.Wildcard &&
                    Math.Abs((endpoint.CreatedUtc - connectionWrittenUtc).TotalSeconds) <= 2)
                    return true;
            }
            return false;
        }

        // Native OWNER_MODULE rows include the socket's creation time. No process
        // handles, packet capture, game memory access or elevation are needed.
        // The app targets x64; both tables align their first row to eight bytes.
        [StructLayout(LayoutKind.Explicit, Size = 160)]
        private struct Udp4Row
        {
            [FieldOffset(0)] public uint Address;
            [FieldOffset(4)] public uint Port;
            [FieldOffset(8)] public uint ProcessId;
            [FieldOffset(16)] public long Created;
        }

        [StructLayout(LayoutKind.Explicit, Size = 176)]
        private struct Udp6Row
        {
            [FieldOffset(0)] public ulong AddressLow;
            [FieldOffset(8)] public ulong AddressHigh;
            [FieldOffset(20)] public uint Port;
            [FieldOffset(24)] public uint ProcessId;
            [FieldOffset(32)] public long Created;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool sorted, uint family, int tableClass, uint reserved);

        internal static List<UdpEndpoint> Read(HashSet<uint> processIds)
        {
            var endpoints = new List<UdpEndpoint>();
            ReadFamily(2, processIds, endpoints);
            ReadFamily(23, processIds, endpoints);
            return endpoints;
        }

        private static void ReadFamily(uint family, HashSet<uint> processIds, List<UdpEndpoint> endpoints)
        {
            const uint insufficientBuffer = 122;
            const uint noData = 232;
            int size = 0;
            uint status = GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 2, 0);
            if (status == noData || (status == 0 && size == 0)) return;
            if (status != 0 && status != insufficientBuffer) throw new Win32Exception((int)status);
            // Other applications can open sockets between the size query and read.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size < 4 || size > 64 * 1024 * 1024) throw new IOException("Invalid UDP table size.");
                int capacity = size;
                IntPtr buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    status = GetExtendedUdpTable(buffer, ref size, false, family, 2, 0);
                    if (status == noData) return;
                    if (status == insufficientBuffer) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    int count = Marshal.ReadInt32(buffer);
                    int stride = family == 2 ? Marshal.SizeOf(typeof(Udp4Row)) : Marshal.SizeOf(typeof(Udp6Row));
                    if (count < 0 || (count > 0 && 8L + (long)count * stride > Math.Min(size, capacity)))
                        throw new IOException("Invalid UDP table layout.");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr address = IntPtr.Add(buffer, 8 + i * stride);
                        uint processId;
                        uint port;
                        long created;
                        bool wildcard;
                        if (family == 2)
                        {
                            var row = (Udp4Row)Marshal.PtrToStructure(address, typeof(Udp4Row));
                            processId = row.ProcessId; port = row.Port; created = row.Created;
                            wildcard = row.Address == 0;
                        }
                        else
                        {
                            var row = (Udp6Row)Marshal.PtrToStructure(address, typeof(Udp6Row));
                            processId = row.ProcessId; port = row.Port; created = row.Created;
                            wildcard = row.AddressLow == 0 && row.AddressHigh == 0;
                        }
                        if (!processIds.Contains(processId)) continue;
                        if (created <= 0 || created > DateTime.MaxValue.ToFileTimeUtc())
                            throw new IOException("Socket creation time is unavailable.");
                        endpoints.Add(new UdpEndpoint {
                            ProcessId = processId,
                            LocalPort = (int)(((port & 255) << 8) | ((port >> 8) & 255)),
                            CreatedUtc = DateTime.FromFileTimeUtc(created),
                            Wildcard = wildcard
                        });
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new IOException("UDP table changed during the scan.");
        }
    }
}
