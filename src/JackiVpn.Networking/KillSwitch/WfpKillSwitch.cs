using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace JackiVpn.Networking.KillSwitch;

public interface IKillSwitch
{
    /// <summary>Installs the filters (replacing any previous set). Throws if anything fails — callers must not connect.</summary>
    void Enable(KillSwitchOptions options);

    /// <summary>Removes every JackiVpn WFP object. Safe to call when nothing is installed.</summary>
    void Disable();

    bool IsEngaged();
}

public sealed class WfpException(string operation, uint code)
    : Win32Exception(unchecked((int)code), $"{operation} failed with 0x{code:X8}.")
{
    public uint Code { get; } = code;
}

/// <summary>
/// Windows Filtering Platform kill switch. Objects are added in a <b>non-dynamic</b> session without the
/// persistent flag: they survive a crash of the core <i>and</i> of the service (fail closed) and disappear
/// only when explicitly removed or when BFE restarts (reboot). Requires administrator/LocalSystem.
/// </summary>
public sealed unsafe partial class WfpKillSwitch(ILogger logger) : IKillSwitch
{
    private const uint RPC_C_AUTHN_WINNT = 10;
    private const uint FWP_E_FILTER_NOT_FOUND = 0x80320003;
    private const uint FWP_E_PROVIDER_NOT_FOUND = 0x80320005;
    private const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
    private const uint FWP_E_NOT_FOUND = 0x80320008;
    private const uint FWP_E_ALREADY_EXISTS = 0x80320009;

    private const uint FWP_ACTION_BLOCK = 0x1001;
    private const uint FWP_ACTION_PERMIT = 0x1002;
    private const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x1;

    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    private static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid CondLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid CondLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid CondRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid CondProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid CondFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    public void Enable(KillSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var plan = KillSwitchPlan.Build(options);
        using var engine = Engine.Open();
        using var arena = new Arena();
        engine.Begin();
        try
        {
            DeleteAllObjects(engine.Handle);

            var providerKey = arena.Struct(KillSwitchPlan.ProviderKey);
            var provider = new FWPM_PROVIDER0
            {
                providerKey = KillSwitchPlan.ProviderKey,
                displayData = Display(arena, Domain.ProductInfo.DisplayName, "Kill switch provider"),
            };
            Check("FwpmProviderAdd0", FwpmProviderAdd0(engine.Handle, &provider, IntPtr.Zero), FWP_E_ALREADY_EXISTS);

            var sublayer = new FWPM_SUBLAYER0
            {
                subLayerKey = KillSwitchPlan.SublayerKey,
                displayData = Display(arena, Domain.ProductInfo.DisplayName, "Kill switch sublayer"),
                providerKey = providerKey,
                weight = KillSwitchPlan.SublayerWeight,
            };
            Check("FwpmSubLayerAdd0", FwpmSubLayerAdd0(engine.Handle, &sublayer, IntPtr.Zero), FWP_E_ALREADY_EXISTS);

            foreach (var spec in plan)
            {
                AddFilter(engine.Handle, arena, spec, providerKey);
            }

            engine.Commit();
            logger.LogWarning("Kill switch engaged: {Count} WFP filters installed (allow LAN: {Lan}).", plan.Count, options.AllowLan);
        }
        catch
        {
            engine.Abort();
            throw;
        }
    }

    public void Disable()
    {
        using var engine = Engine.Open();
        engine.Begin();
        try
        {
            DeleteAllObjects(engine.Handle);
            engine.Commit();
            logger.LogInformation("Kill switch removed.");
        }
        catch
        {
            engine.Abort();
            throw;
        }
    }

    public bool IsEngaged()
    {
        using var engine = Engine.Open();
        var key = KillSwitchPlan.SublayerKey;
        IntPtr sublayer;
        var rc = FwpmSubLayerGetByKey0(engine.Handle, &key, &sublayer);
        if (rc == 0)
        {
            FwpmFreeMemory0(&sublayer);
            return true;
        }

        return false;
    }

    private static void DeleteAllObjects(IntPtr engine)
    {
        for (var i = 0; i < KillSwitchPlan.MaxFilters; i++)
        {
            var key = KillSwitchPlan.FilterKey(i);
            Check("FwpmFilterDeleteByKey0", FwpmFilterDeleteByKey0(engine, &key), FWP_E_FILTER_NOT_FOUND, FWP_E_NOT_FOUND);
        }

        var sub = KillSwitchPlan.SublayerKey;
        Check("FwpmSubLayerDeleteByKey0", FwpmSubLayerDeleteByKey0(engine, &sub), FWP_E_SUBLAYER_NOT_FOUND, FWP_E_NOT_FOUND);
        var prov = KillSwitchPlan.ProviderKey;
        Check("FwpmProviderDeleteByKey0", FwpmProviderDeleteByKey0(engine, &prov), FWP_E_PROVIDER_NOT_FOUND, FWP_E_NOT_FOUND);
    }

    private static void AddFilter(IntPtr engine, Arena arena, FilterSpec spec, IntPtr providerKey)
    {
        var appIds = new List<IntPtr>();
        try
        {
            var conditions = new FWPM_FILTER_CONDITION0[spec.Conditions.Count];
            for (var i = 0; i < conditions.Length; i++)
            {
                conditions[i] = BuildCondition(arena, spec.Conditions[i], spec.Layer, appIds);
            }

            var condPtr = conditions.Length == 0 ? IntPtr.Zero : arena.Array(conditions);
            var filter = new FWPM_FILTER0
            {
                filterKey = spec.Key,
                displayData = Display(arena, $"{Domain.ProductInfo.DisplayName} {spec.Name}", "Kill switch"),
                providerKey = providerKey,
                layerKey = spec.Layer switch
                {
                    WfpLayer.ConnectV4 => LayerConnectV4,
                    WfpLayer.ConnectV6 => LayerConnectV6,
                    WfpLayer.RecvAcceptV4 => LayerRecvAcceptV4,
                    _ => LayerRecvAcceptV6,
                },
                subLayerKey = KillSwitchPlan.SublayerKey,
                weight = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_UINT8, value = spec.Weight },
                numFilterConditions = (uint)conditions.Length,
                filterCondition = condPtr,
                action = new FWPM_ACTION0 { type = spec.Action == FilterAction.Permit ? FWP_ACTION_PERMIT : FWP_ACTION_BLOCK },
            };
            ulong id;
            Check($"FwpmFilterAdd0({spec.Name})", FwpmFilterAdd0(engine, &filter, IntPtr.Zero, &id));
        }
        finally
        {
            foreach (var blob in appIds)
            {
                var b = blob;
                FwpmFreeMemory0(&b);
            }
        }
    }

    private static FWPM_FILTER_CONDITION0 BuildCondition(Arena arena, FilterCondition c, WfpLayer layer, List<IntPtr> appIds)
    {
        switch (c)
        {
            case LoopbackCondition:
                return new FWPM_FILTER_CONDITION0
                {
                    fieldKey = CondFlags,
                    matchType = FWP_MATCH_TYPE.FWP_MATCH_FLAGS_ALL_SET,
                    conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_UINT32, value = FWP_CONDITION_FLAG_IS_LOOPBACK },
                };
            case AppIdCondition app:
            {
                IntPtr blob;
                var path = arena.String(app.ExecutablePath);
                Check($"FwpmGetAppIdFromFileName0({Path.GetFileName(app.ExecutablePath)})", FwpmGetAppIdFromFileName0(path, &blob));
                appIds.Add(blob);
                return new FWPM_FILTER_CONDITION0
                {
                    fieldKey = CondAppId,
                    matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                    conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE, value = (ulong)blob },
                };
            }

            case LocalAddressCondition la:
                return AddressCondition(arena, CondLocalAddress, la.Network, la.PrefixLength, layer);
            case RemoteAddressCondition ra:
                return AddressCondition(arena, CondRemoteAddress, ra.Network, ra.PrefixLength, layer);
            case ProtocolCondition p:
                return new FWPM_FILTER_CONDITION0
                {
                    fieldKey = CondProtocol,
                    matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                    conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_UINT8, value = p.Protocol },
                };
            case LocalPortCondition lp:
                return PortCondition(CondLocalPort, lp.Port);
            case RemotePortCondition rp:
                return PortCondition(CondRemotePort, rp.Port);
            case IcmpTypeCondition icmp:
                return PortCondition(CondLocalPort, icmp.Type);
            default:
                throw new NotSupportedException(c.GetType().Name);
        }
    }

    private static FWPM_FILTER_CONDITION0 PortCondition(Guid field, ushort port) => new()
    {
        fieldKey = field,
        matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_UINT16, value = port },
    };

    private static FWPM_FILTER_CONDITION0 AddressCondition(Arena arena, Guid field, IPAddress network, int prefix, WfpLayer layer)
    {
        if (!KillSwitchPlan.MatchesFamily(network, layer))
        {
            throw new InvalidOperationException($"Address family of {network} does not match layer {layer}.");
        }

        if (KillSwitchPlan.IsV6(layer))
        {
            var v6 = new FWP_V6_ADDR_AND_MASK { prefixLength = (byte)prefix };
            var bytes = network.GetAddressBytes();
            for (var i = 0; i < 16; i++)
            {
                v6.addr[i] = bytes[i];
            }

            return new FWPM_FILTER_CONDITION0
            {
                fieldKey = field,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_V6_ADDR_MASK, value = (ulong)arena.Struct(v6) },
            };
        }

        var b = network.GetAddressBytes();
        var v4 = new FWP_V4_ADDR_AND_MASK
        {
            addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3],
            mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix),
        };
        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = field,
            matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
            conditionValue = new FWP_VALUE0 { type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK, value = (ulong)arena.Struct(v4) },
        };
    }

    private static FWPM_DISPLAY_DATA0 Display(Arena arena, string name, string description) =>
        new() { name = arena.String(name), description = arena.String(description) };

    private static void Check(string operation, uint rc, params uint[] tolerated)
    {
        if (rc != 0 && Array.IndexOf(tolerated, rc) < 0)
        {
            throw new WfpException(operation, rc);
        }
    }

    /// <summary>Owns unmanaged allocations for the duration of one WFP transaction.</summary>
    private sealed class Arena : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];

        public IntPtr String(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            _allocations.Add(p);
            return p;
        }

        public IntPtr Struct<T>(T value)
            where T : unmanaged
        {
            var p = Marshal.AllocHGlobal(sizeof(T));
            *(T*)p = value;
            _allocations.Add(p);
            return p;
        }

        public IntPtr Array<T>(T[] values)
            where T : unmanaged
        {
            var p = Marshal.AllocHGlobal(sizeof(T) * values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                ((T*)p)[i] = values[i];
            }

            _allocations.Add(p);
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _allocations)
            {
                Marshal.FreeHGlobal(p);
            }

            _allocations.Clear();
        }
    }

    private sealed class Engine : IDisposable
    {
        private bool _inTransaction;

        private Engine(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }

        public static Engine Open()
        {
            using var arena = new Arena();
            var session = new FWPM_SESSION0
            {
                displayData = Display(arena, Domain.ProductInfo.DisplayName, "Kill switch session"),
                flags = 0, // non-dynamic: objects outlive this handle (fail closed on crash)
                txnWaitTimeoutInMSec = 10_000,
            };
            IntPtr handle;
            Check("FwpmEngineOpen0", FwpmEngineOpen0(IntPtr.Zero, RPC_C_AUTHN_WINNT, IntPtr.Zero, &session, &handle));
            return new Engine(handle);
        }

        public void Begin()
        {
            Check("FwpmTransactionBegin0", FwpmTransactionBegin0(Handle, 0));
            _inTransaction = true;
        }

        public void Commit()
        {
            Check("FwpmTransactionCommit0", FwpmTransactionCommit0(Handle));
            _inTransaction = false;
        }

        public void Abort()
        {
            if (_inTransaction)
            {
                _ = FwpmTransactionAbort0(Handle);
                _inTransaction = false;
            }
        }

        public void Dispose()
        {
            Abort();
            _ = FwpmEngineClose0(Handle);
        }
    }

    // ---- interop (layouts verified against fwptypes.h / fwpmtypes.h for x64) ----

    private enum FWP_DATA_TYPE : uint
    {
        FWP_EMPTY = 0,
        FWP_UINT8 = 1,
        FWP_UINT16 = 2,
        FWP_UINT32 = 3,
        FWP_UINT64 = 4,
        FWP_BYTE_BLOB_TYPE = 12,
        FWP_V4_ADDR_MASK = 0x100,
        FWP_V6_ADDR_MASK = 0x101,
    }

    private enum FWP_MATCH_TYPE : uint
    {
        FWP_MATCH_EQUAL = 0,
        FWP_MATCH_FLAGS_ALL_SET = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    /// <summary>FWP_VALUE0 / FWP_CONDITION_VALUE0: 4-byte type + 8-byte union at offset 8 (16 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FWP_VALUE0
    {
        [FieldOffset(0)]
        public FWP_DATA_TYPE type;

        [FieldOffset(8)]
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_V4_ADDR_AND_MASK
    {
        public uint addr;
        public uint mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_V6_ADDR_AND_MASK
    {
        public fixed byte addr[16];
        public byte prefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_PROVIDER0
    {
        public Guid providerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public FWP_BYTE_BLOB providerData;
        public IntPtr serviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public FWP_MATCH_TYPE matchType;
        public FWP_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct FWPM_ACTION0
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(4)]
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct FWPM_FILTER0
    {
        [FieldOffset(0)]
        public Guid filterKey;

        [FieldOffset(16)]
        public FWPM_DISPLAY_DATA0 displayData;

        [FieldOffset(32)]
        public uint flags;

        [FieldOffset(40)]
        public IntPtr providerKey;

        [FieldOffset(48)]
        public FWP_BYTE_BLOB providerData;

        [FieldOffset(64)]
        public Guid layerKey;

        [FieldOffset(80)]
        public Guid subLayerKey;

        [FieldOffset(96)]
        public FWP_VALUE0 weight;

        [FieldOffset(112)]
        public uint numFilterConditions;

        [FieldOffset(120)]
        public IntPtr filterCondition;

        [FieldOffset(128)]
        public FWPM_ACTION0 action;

        [FieldOffset(152)]
        public ulong rawContext;

        [FieldOffset(168)]
        public IntPtr reserved;

        [FieldOffset(176)]
        public ulong filterId;

        [FieldOffset(184)]
        public FWP_VALUE0 effectiveWeight;
    }

    internal static int SizeOfFilter => sizeof(FWPM_FILTER0);

    internal static int SizeOfCondition => sizeof(FWPM_FILTER_CONDITION0);

    internal static int SizeOfSublayer => sizeof(FWPM_SUBLAYER0);

    internal static int SizeOfSession => sizeof(FWPM_SESSION0);

    internal static int SizeOfProvider => sizeof(FWPM_PROVIDER0);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, FWPM_SESSION0* session, IntPtr* engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionCommit0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionAbort0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmProviderAdd0(IntPtr engineHandle, FWPM_PROVIDER0* provider, IntPtr sd);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmProviderDeleteByKey0(IntPtr engineHandle, Guid* key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerAdd0(IntPtr engineHandle, FWPM_SUBLAYER0* subLayer, IntPtr sd);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, Guid* key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerGetByKey0(IntPtr engineHandle, Guid* key, IntPtr* subLayer);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterAdd0(IntPtr engineHandle, FWPM_FILTER0* filter, IntPtr sd, ulong* id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterDeleteByKey0(IntPtr engineHandle, Guid* key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmGetAppIdFromFileName0(IntPtr fileName, IntPtr* appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial void FwpmFreeMemory0(IntPtr* p);
}
