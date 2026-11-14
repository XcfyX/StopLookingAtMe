using System.Runtime.InteropServices;

namespace PrivacyScreen;

/// <summary>
/// Minimal Windows Core Audio interop. Only endpoint Mute is written; never changes volume or PnP state.
/// These COM declarations deliberately follow the exact native vtable method order.
/// </summary>
internal static class CoreAudio
{
    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid TopologyIid = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly PropertyKey FormFactorKey = new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PropertyKey AdapterNameKey = new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);
    private const int Render = 0;
    private const int MultimediaRole = 1;
    private const int ConsoleRole = 0;
    private const uint Active = 0x1;
    private const uint ClsCtxAll = 23;
    private const int SpeakersFormFactor = 1;
    private const int PhysicalInternalConnector = 1;
    private const int PhysicalExternalConnector = 2;

    internal sealed record EndpointInfo(string Id, string Name, string Adapter, string TopologyPath,
        int FormFactor, int? ConnectorType, string Evidence);

    internal static IReadOnlyList<EndpointInfo> FindSpeakers(out string selectionReason)
    {
        var all = new List<EndpointInfo>();
        var selected = new List<EndpointInfo>();
        var selectedReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!)
                ?? throw new InvalidOperationException("无法创建 MMDeviceEnumerator");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;

            // Enumerating MUST be independent of the default-endpoint path.  In v1.3 a
            // mistyped IMMDeviceCollection IID made COM unmarshalling throw InvalidCastException
            // here, and the default-endpoint fallback was never reached.
            bool enumerated = TryEnumerate(enumerator, all, seenIds);

            // Scan ALL active render endpoints first, not just the default one.  This is
            // essential when a virtual mixer (e.g. SteelSeries Sonar) is the default but
            // the laptop's Realtek / HDAUDIO speakers are a separate non-default endpoint.
            foreach (var endpoint in all)
            {
                string reason = ClassifyBuiltInSpeaker(endpoint);
                if (reason.StartsWith("select:", StringComparison.Ordinal))
                {
                    selected.Add(endpoint);
                    selectedReasons[endpoint.Id] = reason;
                }
            }

            if (selected.Count == 0)
            {
                // When enumeration fails, GetDefaultAudioEndpoint still works independently.
                // Also use this conservative fallback when enumeration succeeds but the
                // driver's available properties cannot establish a built-in candidate.
                TryDefault(enumerator, MultimediaRole, "eMultimedia", all, seenIds,
                    selected, selectedReasons);
                if (selected.Count == 0)
                    TryDefault(enumerator, ConsoleRole, "eConsole", all, seenIds,
                        selected, selectedReasons);
            }

            // One diagnostic line for EVERY observed endpoint, including virtual devices
            // rejected as unsafe; this makes a Sonar -> Realtek routing setup auditable.
            foreach (var endpoint in all)
            {
                bool chosen = selectedReasons.TryGetValue(endpoint.Id, out string? why);
                string reason = chosen ? why! : ClassifyBuiltInSpeaker(endpoint);
                Log.Write($"Audio: endpoint name={OneLine(endpoint.Name)}; " +
                    $"formFactor={FormatFormFactor(endpoint.FormFactor)}; " +
                    $"adapter={OneLine(endpoint.Adapter)}; " +
                    $"busPath={OneLine(endpoint.TopologyPath)}; " +
                    $"connector={FormatConnector(endpoint.ConnectorType)}; " +
                    $"topology={OneLine(endpoint.Evidence)}; " +
                    $"selected={chosen.ToString().ToLowerInvariant()}; " +
                    $"reason={OneLine(reason)}");
            }

            selectionReason = selected.Count > 0
                ? $"{(enumerated ? "enumerated" : "enumeration failed / default fallback")}: " +
                  string.Join("; ", selected.Select(x => $"{x.Name}: {selectedReasons[x.Id]}"))
                : $"no safe built-in Speakers candidate; enumeration={(enumerated ? "ok" : "failed")}, " +
                  "default eMultimedia/eConsole checked (virtual, USB, Bluetooth, display audio excluded)";
            Log.Write($"Audio: selection result count={selected.Count}; {OneLine(selectionReason)}");
            return selected;
        }
        catch (Exception ex)
        {
            // The mute feature is auxiliary.  Never propagate failures into lock success.
            selectionReason = "Core Audio initialization/selection failed: " + ex.GetType().Name;
            Log.Write("Audio: selection degraded: " + ex);
            return Array.Empty<EndpointInfo>();
        }
        finally { Release(enumeratorObject); }
    }

    private static bool TryEnumerate(IMMDeviceEnumerator enumerator, List<EndpointInfo> all,
        HashSet<string> seenIds)
    {
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator.EnumAudioEndpoints(Render, Active, out collection);
            collection.GetCount(out uint count);
            Log.Write($"Audio: EnumAudioEndpoints(eRender, ACTIVE) count={count}");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? endpoint = null;
                try
                {
                    collection.Item(i, out endpoint);
                    AddEndpoint(endpoint, all, seenIds);
                }
                catch (Exception ex)
                {
                    Log.Write($"Audio: endpoint item={i} inspect failed: {ex}");
                }
                finally { Release(endpoint); }
            }
            return true;
        }
        catch (Exception ex)
        {
            // Includes COMException, InvalidCastException and a broken/missing collection
            // interface.  Do NOT return before attempting GetDefaultAudioEndpoint.
            Log.Write($"Audio: EnumAudioEndpoints failed; trying default endpoints: {ex}");
            return false;
        }
        finally { Release(collection); }
    }

    private static void TryDefault(IMMDeviceEnumerator enumerator, int role, string roleName,
        List<EndpointInfo> all, HashSet<string> seenIds, List<EndpointInfo> selected,
        Dictionary<string, string> selectedReasons)
    {
        IMMDevice? device = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(Render, role, out device);
            var info = Describe(device); // Does NOT rely on IMMDeviceCollection.
            if (seenIds.Add(info.Id)) all.Add(info);

            // A default device is NOT necessarily an internal speaker.  Sonar/Voicemeeter
            // must never be muted merely because Windows routes all system sound through it.
            string classification = ClassifyBuiltInSpeaker(info);
            string decision = DefaultEligibility(info);
            if (decision.StartsWith("select:", StringComparison.Ordinal))
            {
                selected.Add(info);
                selectedReasons[info.Id] = $"default {roleName} fallback: {decision}";
            }
            Log.Write($"Audio: default {roleName} name={OneLine(info.Name)}; " +
                $"formFactor={FormatFormFactor(info.FormFactor)}; " +
                $"decision={OneLine(decision)}; physicalAssessment={OneLine(classification)}");
        }
        catch (Exception ex)
        {
            Log.Write($"Audio: GetDefaultAudioEndpoint({roleName}) failed: {ex}");
        }
        finally { Release(device); }
    }

    private static void AddEndpoint(IMMDevice device, List<EndpointInfo> all, HashSet<string> seenIds)
    {
        var info = Describe(device);
        if (seenIds.Add(info.Id)) all.Add(info);
    }

    private static string ClassifyBuiltInSpeaker(EndpointInfo endpoint)
    {
        if (endpoint.FormFactor != SpeakersFormFactor)
            return "skip: not FormFactor=Speakers";

        // A PnP bus ID identifies the device transport. An IConnector type does not:
        // Physical_External describes a jack/pin exposed by the audio driver, and laptop
        // built-in speakers may legitimately have this value (as seen on Realtek HDAUDIO).
        // Reject positively identified external/virtual transports before considering an
        // internal bus. These are PnP/device-path facts, not connector-type guesses.
        string? externalBus = StrongExternalOrVirtualEvidence(endpoint);
        if (externalBus is not null)
            return $"skip: explicit external/virtual/display evidence {externalBus}";

        string? internalBus = StrongInternalBusEvidence(endpoint.TopologyPath, endpoint.Adapter);
        if (internalBus is not null)
            return $"select: strong internal evidence bus={internalBus}; " +
                   $"connector={FormatConnector(endpoint.ConnectorType)} does not override device bus";

        // No hard internal bus evidence: retain v1.3.1's conservative connector/adapter
        // assessment. Never use Physical_External to veto an already proven internal bus.
        if (endpoint.ConnectorType.HasValue &&
            endpoint.ConnectorType.Value != PhysicalInternalConnector &&
            endpoint.ConnectorType.Value != 0 /* Unknown_Connector */)
            return $"skip: no verified internal bus; connector {FormatConnector(endpoint.ConnectorType)} is not internal";
        if (endpoint.ConnectorType == PhysicalInternalConnector &&
            LooksBuiltInAdapter(endpoint.Adapter, endpoint.Name))
            return "select: Speakers with Physical_Internal connector and known laptop audio adapter";
        if (LooksBuiltInAdapter(endpoint.Adapter, endpoint.Name))
            // An adapter friendly name is an inference, not PnP bus proof.
            return "select: probable built-in audio adapter (no explicit external evidence; bus unverified)";
        return "skip: insufficient internal-bus / adapter evidence";
    }

    private static string DefaultEligibility(EndpointInfo endpoint)
    {
        if (endpoint.FormFactor != SpeakersFormFactor)
            return "skip: default endpoint is not Speakers";
        string? externalBus = StrongExternalOrVirtualEvidence(endpoint);
        if (externalBus is not null)
            return $"skip: default has explicit external/virtual/display evidence {externalBus}";
        string? internalBus = StrongInternalBusEvidence(endpoint.TopologyPath, endpoint.Adapter);
        if (internalBus is not null)
            return $"select: default Speakers with strong internal evidence bus={internalBus}; " +
                   $"connector={FormatConnector(endpoint.ConnectorType)} does not override device bus";
        if (endpoint.ConnectorType.HasValue &&
            endpoint.ConnectorType.Value != PhysicalInternalConnector &&
            endpoint.ConnectorType.Value != 0)
            return "skip: default has non-internal connector and no verified internal bus";
        return "select: default Speakers, not identified as external (built-in unverified)";
    }

    private static bool LooksBuiltInAdapter(string adapter, string name)
    {
        string text = (adapter + " " + name).ToUpperInvariant();
        return new[] { "REALTEK", "CONEXANT", "CIRRUS LOGIC", "INTEL SMART SOUND",
            "INTEL(R) SMART SOUND", "SYNAPTICS", "ESS AUDIO", "HIGH DEFINITION AUDIO DEVICE" }
            .Any(text.Contains);
    }

    private static string FormatFormFactor(int value) => value switch
    {
        0 => "RemoteNetworkDevice(0)", 1 => "Speakers(1)", 2 => "LineLevel(2)",
        3 => "Headphones(3)", 4 => "Microphone(4)", 5 => "Headset(5)",
        6 => "Handset(6)", 7 => "UnknownDigitalPassthrough(7)",
        8 => "SPDIF(8)", 9 => "DigitalAudioDisplayDevice(9)",
        10 => "UnknownFormFactor(10)", _ => $"Unknown({value})"
    };

    private static string FormatConnector(int? value) => value switch
    {
        null => "unavailable", 0 => "Unknown(0)", 1 => "Physical_Internal(1)",
        2 => "Physical_External(2)", 3 => "Software_IO(3)",
        4 => "Software_Fixed(4)", 5 => "Network(5)", _ => $"Other({value})"
    };

    private static string OneLine(string? value) => (value ?? "")
        .Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static EndpointInfo Describe(IMMDevice endpoint)
    {
        string id = ReadId(endpoint);
        IPropertyStore? props = null;
        try
        {
            endpoint.OpenPropertyStore(0, out props); // STGM_READ
            string name = ReadString(props, FriendlyNameKey);
            string adapter = ReadString(props, AdapterNameKey);
            int form = ReadNumber(props, FormFactorKey, -1);
            string path = "";
            int? connectorType = null;
            string evidence = "topology unavailable";
            object? topologyObj = null;
            IConnector? endpointConnector = null;
            IConnector? adapterConnector = null;
            try
            {
                Guid iid = TopologyIid;
                endpoint.Activate(ref iid, ClsCtxAll, nint.Zero, out topologyObj);
                var topology = (IDeviceTopology)topologyObj;
                topology.GetConnectorCount(out uint n);
                if (n > 0)
                {
                    topology.GetConnector(0, out endpointConnector);
                    // This is the connected adapter's device-topology ID, not the endpoint ID.
                    try
                    {
                        endpointConnector.GetDeviceIdConnectedTo(out nint ptr);
                        try { if (ptr != nint.Zero) path = Marshal.PtrToStringUni(ptr) ?? ""; }
                        finally { if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr); }
                    }
                    catch (COMException) { /* drivers may not expose a connected adapter ID */ }
                    endpointConnector.GetConnectedTo(out adapterConnector);
                    adapterConnector.GetType(out int kind);
                    connectorType = kind;
                    evidence = $"adapter connector type={kind}";
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                evidence = "topology not exposed: " + ex.GetType().Name;
            }
            finally
            {
                Release(adapterConnector);
                Release(endpointConnector);
                Release(topologyObj);
            }
            return new EndpointInfo(id, name.Length > 0 ? name : "Unknown speakers", adapter,
                path, form, connectorType, evidence);
        }
        finally { Release(props); }
    }

    // The topology connection's device ID normally contains the adapter PnP path.
    // Adapter is normally a friendly name, but on some drivers may carry a device ID;
    // inspect both without treating the friendly name "Realtek" as hard bus proof.
    private static string? StrongInternalBusEvidence(string topologyPath, string adapter)
    {
        string path = (topologyPath + " " + adapter).ToUpperInvariant();
        if (path.Contains("HDAUDIO#") || path.Contains("HDAUDIO\\")) return "hdaudio";
        if (path.Contains("INTELAUDIO#") || path.Contains("INTELAUDIO\\")) return "intelaudio";
        if (path.Contains("ACPI#") || path.Contains("ACPI\\")) return "acpi";
        return null;
    }

    // Only actual external/virtual PnP transport evidence is a hard veto. The
    // adapter-side Physical_External connector describes a jack/pin, NOT the bus.
    // Check these first to avoid selecting e.g. a USB device with a misleading name.
    private static string? StrongExternalOrVirtualEvidence(EndpointInfo device)
    {
        string path = (device.TopologyPath + " " + device.Adapter).ToUpperInvariant();
        if (path.Contains("USB#") || path.Contains("USB\\")) return "bus=usb";
        if (path.Contains("BTHENUM") || path.Contains("BTHHFENUM") ||
            path.Contains("BTHLEENUM")) return "bus=bluetooth";
        if (path.Contains("ROOT#MEDIA") || path.Contains("ROOT\\MEDIA"))
            return "bus=root#media";

        string adapter = (device.Adapter + " " + device.Name).ToUpperInvariant();
        bool displayAdapter = adapter.Contains("NVIDIA HIGH DEFINITION AUDIO") ||
            adapter.Contains("AMD HIGH DEFINITION AUDIO") ||
            adapter.Contains("AMD DISPLAY AUDIO") ||
            adapter.Contains("INTEL(R) DISPLAY AUDIO") ||
            adapter.Contains("INTEL DISPLAY AUDIO");
        bool displayLink = adapter.Contains("HDMI") || adapter.Contains("DISPLAYPORT") ||
            adapter.Contains("DISPLAY PORT") || adapter.Contains("DISPLAY AUDIO") ||
            path.Contains("HDMI") || path.Contains("DISPLAYPORT") ||
            path.Contains("DISPLAY PORT") || path.Contains("MONITOR#") ||
            path.Contains("MONITOR\\");
        if (displayAdapter && displayLink) return "display-audio adapter/link";
        return null;
    }

    internal static bool GetMute(string endpointId)
    {
        return WithEndpointVolume(endpointId, volume =>
        {
            volume.GetMute(out bool muted);
            return muted;
        });
    }

    internal static void SetMute(string endpointId, bool muted)
    {
        WithEndpointVolume(endpointId, volume =>
        {
            volume.SetMute(muted, nint.Zero);
            return 0;
        });
    }

    private static T WithEndpointVolume<T>(string id, Func<IAudioEndpointVolume, T> action)
    {
        object? enumeratorObj = null;
        IMMDevice? device = null;
        object? volumeObj = null;
        try
        {
            enumeratorObj = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!)
                ?? throw new InvalidOperationException("无法创建 MMDeviceEnumerator");
            ((IMMDeviceEnumerator)enumeratorObj).GetDevice(id, out device);
            Guid iid = EndpointVolumeIid;
            device.Activate(ref iid, ClsCtxAll, nint.Zero, out volumeObj);
            return action((IAudioEndpointVolume)volumeObj);
        }
        finally { Release(volumeObj); Release(device); Release(enumeratorObj); }
    }

    private static string ReadId(IMMDevice device)
    {
        device.GetId(out nint ptr);
        try { return Marshal.PtrToStringUni(ptr) ?? ""; }
        finally { if (ptr != nint.Zero) Marshal.FreeCoTaskMem(ptr); }
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out PropVariant pv);
        try { return pv.Vt == 31 ? Marshal.PtrToStringUni(pv.Ptr) ?? "" : ""; } // VT_LPWSTR
        finally { PropVariantClear(ref pv); }
    }

    private static int ReadNumber(IPropertyStore store, PropertyKey key, int fallback)
    {
        store.GetValue(ref key, out PropVariant pv);
        try { return pv.Vt == 19 ? unchecked((int)pv.UInt) : fallback; } // VT_UI4
        finally { PropVariantClear(ref pv); }
    }

    private static void Release(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid guid, uint id) { FormatId = guid; PropertyId = id; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public nint Ptr;
        [FieldOffset(8)] public uint UInt;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection collection);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(nint client);
        void UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId(out nint id);
        void GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint callback);
        void UnregisterControlChangeNotify(nint callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, nint context);
        void SetMasterVolumeLevelScalar(float level, nint context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, nint context);
        void SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        void GetVolumeStepInfo(out uint step, out uint count);
        void VolumeStepUp(nint context);
        void VolumeStepDown(nint context);
        void QueryHardwareSupport(out uint mask);
        void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        void GetConnectorCount(out uint count);
        void GetConnector(uint index, out IConnector connector);
        void GetSubunitCount(out uint count);
        void GetSubunit(uint index, out nint subunit);
        void GetPartById(uint id, out nint part);
        void GetDeviceId(out nint id);
        void GetSignalPath(nint from, nint to, [MarshalAs(UnmanagedType.Bool)] bool rejectMixedPaths, out nint path);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        void GetType(out int kind);
        void GetDataFlow(out int dataFlow);
        void ConnectTo(IConnector other);
        void Disconnect();
        void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        void GetConnectedTo(out IConnector other);
        void GetConnectorIdConnectedTo(out nint connectorId);
        void GetDeviceIdConnectedTo(out nint deviceId);
    }
}
