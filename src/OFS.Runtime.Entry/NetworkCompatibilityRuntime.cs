using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal static class NetworkCompatibilityRuntime
{
    private static readonly string FrameworkVersion =
        ModManifestValidator.CurrentSdkVersion.ToString(3);
    private const string ProtocolKey = "OFS.Protocol";
    private const string ProfileKey = "OFS.Profile";
    private const string ModsKey = "OFS.Mods";
    private const string AuthKey = "OFS.Auth";
    private const string MemberProtocolKey = "OFS.Member.Protocol";
    private const string MemberProfileKey = "OFS.Member.Profile";
    private const string MemberModsKey = "OFS.Member.Mods";
    private const int RepublishIntervalFrames = 300;
    private const long AuthenticationTimeoutMilliseconds = 10_000;

    private static readonly NetworkCompatibilityProfile UnconfiguredProfile =
        NetworkCompatibilityProfiles.Create("unconfigured", FrameworkVersion, []);
    private static readonly NetworkCompatibilityResult UnknownCheck = new(
        NetworkCompatibilityStatus.Unknown,
        true,
        "OFS multiplayer compatibility has not been checked yet.");

    private static IUnsafeIl2CppApi? _api;
    private static NetworkCompatibilityProfile _profile = UnconfiguredProfile;
    private static NetworkCompatibilityResult _lastCheck = UnknownCheck;
    private static NetworkRemediationPlan? _lastRemediationPlan;
    private static nint _steamLobbyManagerClass;
    private static nint _lobbyDataClass;
    private static nint _lobbyMemberDataClass;
    private static nint _networkConnectionToClientClass;
    private static nint _networkConnectionClass;
    private static nint _networkManagerClass;
    private static nint _basicAuthenticatorClass;
    private static nint _componentClass;
    private static nint _mainMenuManagerClass;
    private static nint _loadingManagerClass;
    private static nint _getSteamLobbyManagerInstance;
    private static nint _getIsInLobby;
    private static nint _getIsHost;
    private static nint _currentLobbyField;
    private static nint _getLobbyMetadata;
    private static nint _setLobbyMetadata;
    private static nint _setMemberMetadata;
    private static nint _getLobbyMember;
    private static nint _getLobbyMemberMetadata;
    private static nint _leaveLobby;
    private static nint _getMainMenuManagerInstance;
    private static nint _showVersionMismatchPopup;
    private static nint _hideAllLoadingsImmediate;
    private static nint _getConnectionAddress;
    private static nint _disconnectConnection;
    private static nint _getComponentGameObject;
    private static nint _networkManagerAuthenticatorField;
    private static nint _basicServerUsernameField;
    private static nint _basicServerPasswordField;
    private static nint _basicClientUsernameField;
    private static nint _basicClientPasswordField;
    private static nint _connectionAuthenticatedField;
    private static nint _startHostOriginal;
    private static nint _startClientOriginal;
    private static nint _serverConnectOriginal;
    private static nint _serverConnectInternalOriginal;
    private static nint _serverDisconnectOriginal;
    private static NativeDetour? _startHostHook;
    private static NativeDetour? _startClientHook;
    private static NativeDetour? _serverConnectHook;
    private static NativeDetour? _serverConnectInternalHook;
    private static NativeDetour? _serverDisconnectHook;
    private static nint _lastPublishedManager;
    private static int _nextPublishFrame;
    private static nint _serverAuthenticatorManager;
    private static readonly Dictionary<nint, nint> OwnedAuthenticators = new();
    private static readonly Dictionary<nint, long> PendingAuthentications = new();
    private static int _automaticRemediationStarted;

    public static NetworkCompatibilityProfile Profile => Volatile.Read(ref _profile);
    public static NetworkCompatibilityResult LastCheck => Volatile.Read(ref _lastCheck);
    public static NetworkRemediationPlan? LastRemediationPlan =>
        Volatile.Read(ref _lastRemediationPlan);

    public static void Configure(
        IUnsafeIl2CppApi api,
        string gameDirectory,
        IEnumerable<ModManifest> loadedManifests)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(loadedManifests);

        var identities = loadedManifests.Select(manifest => new NetworkModIdentity(
            manifest.Id,
            manifest.Version,
            manifest.Multiplayer));
        var profile = NetworkCompatibilityProfiles.Create(
            ReadGameFingerprint(gameDirectory),
            FrameworkVersion,
            identities);
        Volatile.Write(ref _profile, profile);
        Volatile.Write(ref _lastRemediationPlan, null);
        Interlocked.Exchange(ref _automaticRemediationStarted, 0);
        _api = api;

        RuntimeLog.Write(
            $"OFS network profile: protocol={profile.ProtocolVersion}, " +
            $"fingerprint={profile.Fingerprint}, required={profile.RequiredMods.Count}, " +
            $"incompatible={profile.IncompatibleMods.Count}.");

        try
        {
            ResolveMethods(api);
            InstallHooks(api);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastCheck, new NetworkCompatibilityResult(
                NetworkCompatibilityStatus.Error,
                !profile.RequiresExactMatch && !profile.BlocksMultiplayer,
                $"OFS multiplayer compatibility hooks are unavailable: {exception.Message}"));
            RuntimeLog.Write($"OFS network compatibility hooks unavailable: {exception}");
        }
    }

    public static void Poll(FrameEvent frame)
    {
        if (_api is null || _getSteamLobbyManagerInstance == 0)
        {
            return;
        }

        try
        {
            PollAuthenticationTimeouts();
            var manager = GetSteamLobbyManager();
            if (manager == 0 || !ReadBoolean(_getIsInLobby, manager) || !ReadBoolean(_getIsHost, manager))
            {
                _lastPublishedManager = 0;
                return;
            }

            if (manager != _lastPublishedManager || frame.FrameCount >= _nextPublishFrame)
            {
                PublishHostProfile(manager);
                _lastPublishedManager = manager;
                _nextPublishFrame = unchecked(frame.FrameCount + RepublishIntervalFrames);
            }
        }
        catch (Exception exception)
        {
            SafeLog($"OFS lobby metadata polling failed: {exception.Message}");
        }
    }

    private static void ResolveMethods(IUnsafeIl2CppApi api)
    {
        _steamLobbyManagerClass = RequireClass(api, "Assembly-CSharp.dll", "", "SteamLobbyManager");
        _lobbyDataClass = RequireClass(
            api,
            "Heathen.Steamworks.dll",
            "Heathen.SteamworksIntegration",
            "LobbyData");
        _lobbyMemberDataClass = RequireClass(
            api,
            "Heathen.Steamworks.dll",
            "Heathen.SteamworksIntegration",
            "LobbyMemberData");
        _networkConnectionToClientClass = RequireClass(
            api,
            "Mirror.dll",
            "Mirror",
            "NetworkConnectionToClient");
        _networkConnectionClass = RequireClass(
            api,
            "Mirror.dll",
            "Mirror",
            "NetworkConnection");
        _networkManagerClass = RequireClass(api, "Mirror.dll", "Mirror", "NetworkManager");
        _basicAuthenticatorClass = RequireClass(
            api,
            "Mirror.Authenticators.dll",
            "Mirror.Authenticators",
            "BasicAuthenticator");
        _componentClass = RequireClass(
            api,
            "UnityEngine.CoreModule.dll",
            "UnityEngine",
            "Component");
        _mainMenuManagerClass = RequireClass(api, "Assembly-CSharp.dll", "", "MainMenuManager");
        _loadingManagerClass = RequireClass(api, "Assembly-CSharp.dll", "", "LoadingManagerUI");

        _getSteamLobbyManagerInstance = RequireMethod(api, _steamLobbyManagerClass, "get_Instance", 0);
        _getIsInLobby = RequireMethod(api, _steamLobbyManagerClass, "get_IsInLobby", 0);
        _getIsHost = RequireMethod(api, _steamLobbyManagerClass, "get_IsHost", 0);
        _currentLobbyField = RequireField(api, _steamLobbyManagerClass, "currentLobby");
        _getLobbyMetadata = RequireMethod(api, _lobbyDataClass, "get_Item", 1);
        _setLobbyMetadata = RequireMethod(api, _lobbyDataClass, "set_Item", 2);
        _setMemberMetadata = RequireMethod(api, _lobbyDataClass, "SetMemberMetadata", 2);
        _getLobbyMember = RequireMethod(api, _lobbyMemberDataClass, "Get", 2);
        _getLobbyMemberMetadata = RequireMethod(api, _lobbyMemberDataClass, "get_Item", 1);
        _leaveLobby = RequireMethod(api, _steamLobbyManagerClass, "LeaveLobby", 0);
        _getMainMenuManagerInstance = RequireMethod(api, _mainMenuManagerClass, "get_Instance", 0);
        _showVersionMismatchPopup = RequireMethod(
            api,
            _mainMenuManagerClass,
            "ShowVersionMismatchPopup",
            2);
        _hideAllLoadingsImmediate = RequireMethod(
            api,
            _loadingManagerClass,
            "HideAllImmediate",
            0);
        _getConnectionAddress = RequireMethod(
            api,
            _networkConnectionToClientClass,
            "get_address",
            0);
        _disconnectConnection = RequireMethod(
            api,
            _networkConnectionToClientClass,
            "Disconnect",
            0);
        _getComponentGameObject = RequireMethod(api, _componentClass, "get_gameObject", 0);
        _networkManagerAuthenticatorField = RequireField(
            api,
            _networkManagerClass,
            "authenticator");
        _basicServerUsernameField = RequireField(
            api,
            _basicAuthenticatorClass,
            "serverUsername");
        _basicServerPasswordField = RequireField(
            api,
            _basicAuthenticatorClass,
            "serverPassword");
        _basicClientUsernameField = RequireField(
            api,
            _basicAuthenticatorClass,
            "username");
        _basicClientPasswordField = RequireField(
            api,
            _basicAuthenticatorClass,
            "password");
        _connectionAuthenticatedField = RequireField(
            api,
            _networkConnectionClass,
            "isAuthenticated");
    }

    private static unsafe void InstallHooks(IUnsafeIl2CppApi api)
    {
        var networkManagerClass = RequireClass(api, "Assembly-CSharp.dll", "", "NewNetworkManager");
        var startHost = RequireMethod(api, networkManagerClass, "StartHostSafe", 0);
        var startClient = RequireMethod(api, networkManagerClass, "StartClientSafe", 1);
        var serverConnect = RequireMethod(api, networkManagerClass, "OnServerConnect", 1);
        var serverDisconnect = RequireMethod(api, networkManagerClass, "OnServerDisconnect", 1);
        var serverConnectInternal = RequireMethod(
            api,
            _networkManagerClass,
            "OnServerConnectInternal",
            1);

        _startHostHook = HookRuntime.Install(
            "ofs.framework",
            new NativeDetourDefinition(
                "framework.network-profile-host",
                RequireMethodPointer(api, startHost),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnStartHost));
        _startHostOriginal = _startHostHook.Original;
        try
        {
            _startClientHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.network-profile-client",
                    RequireMethodPointer(api, startClient),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnStartClient));
            _startClientOriginal = _startClientHook.Original;
            _serverConnectHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.network-profile-server-connect",
                    RequireMethodPointer(api, serverConnect),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnServerConnect));
            _serverConnectOriginal = _serverConnectHook.Original;
            _serverConnectInternalHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.network-auth-connect-internal",
                    RequireMethodPointer(api, serverConnectInternal),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnServerConnectInternal));
            _serverConnectInternalOriginal = _serverConnectInternalHook.Original;
            _serverDisconnectHook = HookRuntime.Install(
                "ofs.framework",
                new NativeDetourDefinition(
                    "framework.network-auth-disconnect",
                    RequireMethodPointer(api, serverDisconnect),
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnServerDisconnect));
            _serverDisconnectOriginal = _serverDisconnectHook.Original;
        }
        catch
        {
            _serverDisconnectHook?.Remove();
            _serverDisconnectHook = null;
            _serverDisconnectOriginal = 0;
            _serverConnectInternalHook?.Remove();
            _serverConnectInternalHook = null;
            _serverConnectInternalOriginal = 0;
            _serverConnectHook?.Remove();
            _serverConnectHook = null;
            _serverConnectOriginal = 0;
            _startClientHook?.Remove();
            _startClientHook = null;
            _startClientOriginal = 0;
            _startHostHook.Remove();
            _startHostHook = null;
            _startHostOriginal = 0;
            throw;
        }

        RuntimeLog.Write(
            $"OFS network compatibility hooks installed: " +
            $"host=0x{_startHostHook.Target:X}, client=0x{_startClientHook.Target:X}, " +
            $"serverConnect=0x{_serverConnectHook.Target:X}, " +
            $"connectInternal=0x{_serverConnectInternalHook.Target:X}, " +
            $"serverDisconnect=0x{_serverDisconnectHook.Target:X}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStartHost(nint instance, nint methodInfo)
    {
        try
        {
            var profile = Profile;
            var singlePlayer = SaveLifecycleRuntime.IsSinglePlayerMode();
            RuntimeLog.Write(
                $"OFS host-start classification: singlePlayer={singlePlayer}, " +
                $"blocksMultiplayer={profile.BlocksMultiplayer}.");
            if (ShouldBlockHostStart(profile.BlocksMultiplayer, singlePlayer))
            {
                var result = NetworkCompatibilityProfiles.CompareHost(
                    profile,
                    profile.ProtocolVersion.ToString(),
                    profile.Fingerprint);
                BlockNetworking(result);
                return;
            }

            if (singlePlayer)
            {
                ClearDedicatedAuthenticator(instance);
                Volatile.Write(ref _lastCheck, new NetworkCompatibilityResult(
                    NetworkCompatibilityStatus.Compatible,
                    true,
                    "Single-player local host is exempt from multiplayer mod compatibility."));
                RuntimeLog.Write(
                    "OFS local single-player host permitted; multiplayer compatibility " +
                    "policy was not applied.");
                var singlePlayerOriginal =
                    (delegate* unmanaged[Cdecl]<nint, nint, void>)_startHostOriginal;
                singlePlayerOriginal(instance, methodInfo);
                return;
            }

            if (profile.RequiresExactMatch)
            {
                ConfigureDedicatedAuthenticator(instance, isServer: true);
            }
            else
            {
                ClearDedicatedAuthenticator(instance);
            }

            var lobbyManager = GetSteamLobbyManager();
            if (lobbyManager != 0 && ReadBoolean(_getIsInLobby, lobbyManager))
            {
                PublishHostProfile(lobbyManager);
            }
            Volatile.Write(ref _lastCheck, new NetworkCompatibilityResult(
                NetworkCompatibilityStatus.Compatible,
                true,
                "Local OFS profile permits hosting."));

            var original = (delegate* unmanaged[Cdecl]<nint, nint, void>)_startHostOriginal;
            original(instance, methodInfo);
        }
        catch (Exception exception)
        {
            SafeLog($"OFS host compatibility bridge failed closed: {exception}");
            BlockNetworking(new NetworkCompatibilityResult(
                NetworkCompatibilityStatus.Error,
                false,
                $"Could not publish the OFS host profile: {exception.Message}"));
        }
    }

    internal static bool ShouldBlockHostStart(bool blocksMultiplayer, bool singlePlayer) =>
        blocksMultiplayer && !singlePlayer;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStartClient(nint instance, nint address, nint methodInfo)
    {
        try
        {
            var lobbyManager = GetSteamLobbyManager();
            string? protocol = null;
            string? fingerprint = null;
            string? authMode = null;
            string? requiredMods = null;
            if (lobbyManager != 0 && ReadBoolean(_getIsInLobby, lobbyManager))
            {
                PublishMemberProfile(lobbyManager);
                protocol = ReadLobbyMetadata(lobbyManager, ProtocolKey);
                fingerprint = ReadLobbyMetadata(lobbyManager, ProfileKey);
                authMode = ReadLobbyMetadata(lobbyManager, AuthKey);
                requiredMods = ReadLobbyMetadata(lobbyManager, ModsKey);
            }

            var result = NetworkCompatibilityProfiles.CompareHost(
                Profile,
                protocol,
                fingerprint,
                requiredMods);
            Volatile.Write(ref _lastCheck, result);
            UpdateClientRemediationPlan(result);
            RuntimeLog.Write(
                $"OFS client compatibility preflight: status={result.Status}, " +
                $"allowed={result.Allowed}, host={result.HostFingerprint ?? "<none>"}.");
            LogModDifferences("host", result);
            if (!result.Allowed)
            {
                BlockNetworking(result);
                return;
            }

            if (string.Equals(
                    authMode,
                    NetworkAuthenticationProfiles.MirrorBasicMode,
                    StringComparison.Ordinal))
            {
                ConfigureDedicatedAuthenticator(instance, isServer: false);
            }
            else
            {
                ClearDedicatedAuthenticator(instance);
            }

            var original = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_startClientOriginal;
            original(instance, address, methodInfo);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastRemediationPlan, null);
            var profile = Profile;
            var failOpen = !profile.RequiresExactMatch && !profile.BlocksMultiplayer;
            var result = new NetworkCompatibilityResult(
                NetworkCompatibilityStatus.Error,
                failOpen,
                $"OFS client compatibility preflight failed: {exception.Message}");
            Volatile.Write(ref _lastCheck, result);
            SafeLog(result.Message);
            if (!failOpen)
            {
                BlockNetworking(result);
                return;
            }

            var original = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_startClientOriginal;
            original(instance, address, methodInfo);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnServerConnect(nint instance, nint connection, nint methodInfo)
    {
        try
        {
            var address = ReadConnectionAddress(connection);
            if (_serverAuthenticatorManager == instance)
            {
                PendingAuthentications.Remove(connection);
                Volatile.Write(ref _lastCheck, new NetworkCompatibilityResult(
                    NetworkCompatibilityStatus.Compatible,
                    true,
                    "Peer passed the dedicated OFS Mirror authenticator.",
                    Profile.Fingerprint));
                SafeLog($"OFS Mirror authentication accepted peer '{address}'.");
                CallOriginalServerConnect(instance, connection, methodInfo);
                return;
            }
            if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                CallOriginalServerConnect(instance, connection, methodInfo);
                return;
            }

            string? protocol = null;
            string? fingerprint = null;
            string? requiredMods = null;
            var manager = GetSteamLobbyManager();
            if (manager != 0 &&
                ReadBoolean(_getIsInLobby, manager) &&
                TryParseSteamId(address, out var steamId))
            {
                protocol = ReadMemberMetadata(manager, steamId, MemberProtocolKey);
                fingerprint = ReadMemberMetadata(manager, steamId, MemberProfileKey);
                requiredMods = ReadMemberMetadata(manager, steamId, MemberModsKey);
            }

            var result = NetworkCompatibilityProfiles.ComparePeer(
                Profile,
                protocol,
                fingerprint,
                requiredMods);
            Volatile.Write(ref _lastCheck, result);
            SafeLog(
                $"OFS server peer preflight: address='{address}', status={result.Status}, " +
                $"allowed={result.Allowed}, peer={result.HostFingerprint ?? "<none>"}.");
            LogModDifferences("peer", result);
            if (!result.Allowed)
            {
                DisconnectPeer(connection, result);
                return;
            }

            CallOriginalServerConnect(instance, connection, methodInfo);
        }
        catch (Exception exception)
        {
            var profile = Profile;
            var failOpen = !profile.RequiresExactMatch && !profile.BlocksMultiplayer;
            SafeLog($"OFS server peer preflight failed (failOpen={failOpen}): {exception}");
            if (failOpen)
            {
                CallOriginalServerConnect(instance, connection, methodInfo);
            }
            else
            {
                DisconnectPeer(connection, new NetworkCompatibilityResult(
                    NetworkCompatibilityStatus.Error,
                    false,
                    $"Host could not validate peer OFS profile: {exception.Message}"));
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnServerConnectInternal(
        nint instance,
        nint connection,
        nint methodInfo)
    {
        try
        {
            var original = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                _serverConnectInternalOriginal;
            original(instance, connection, methodInfo);
            if (_serverAuthenticatorManager == instance && connection != 0)
            {
                if (RequireApi().ReadBoolean(connection, _connectionAuthenticatedField))
                {
                    PendingAuthentications.Remove(connection);
                    SafeLog(
                        $"OFS Mirror authentication completed synchronously for " +
                        $"'{ReadConnectionAddress(connection)}'.");
                }
                else
                {
                    PendingAuthentications[connection] =
                        Environment.TickCount64 + AuthenticationTimeoutMilliseconds;
                    SafeLog(
                        $"OFS Mirror authentication started for '{ReadConnectionAddress(connection)}'.");
                }
            }
        }
        catch (Exception exception)
        {
            SafeLog($"OFS raw server-connect authentication bridge failed: {exception}");
            DisconnectPeer(connection, new NetworkCompatibilityResult(
                NetworkCompatibilityStatus.Error,
                false,
                $"Host could not start OFS Mirror authentication: {exception.Message}"));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnServerDisconnect(nint instance, nint connection, nint methodInfo)
    {
        try
        {
            PendingAuthentications.Remove(connection);
            var original = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                _serverDisconnectOriginal;
            original(instance, connection, methodInfo);
        }
        catch (Exception exception)
        {
            SafeLog($"OFS server-disconnect authentication bridge failed: {exception}");
        }
    }

    private static void PublishHostProfile(nint manager)
    {
        var profile = Profile;
        WriteLobbyMetadata(manager, ProtocolKey, profile.ProtocolVersion.ToString());
        WriteLobbyMetadata(manager, ProfileKey, profile.Fingerprint);
        WriteLobbyMetadata(
            manager,
            AuthKey,
            _serverAuthenticatorManager != 0
                ? NetworkAuthenticationProfiles.MirrorBasicMode
                : "none");
        WriteLobbyMetadata(
            manager,
            ModsKey,
            NetworkProfileMetadata.EncodeRequiredMods(profile.RequiredMods));
        SafeLog(
            $"OFS host profile published: protocol={profile.ProtocolVersion}, " +
            $"fingerprint={profile.Fingerprint}.");
    }

    private static void ConfigureDedicatedAuthenticator(nint manager, bool isServer)
    {
        var api = RequireApi();
        if (manager == 0)
        {
            throw new ArgumentException("NetworkManager instance is null.", nameof(manager));
        }

        if (!OwnedAuthenticators.TryGetValue(manager, out var authenticator))
        {
            var current = api.ReadObjectReference(manager, _networkManagerAuthenticatorField);
            if (current != 0)
            {
                throw new InvalidOperationException(
                    "NewNetworkManager already has a non-OFS authenticator; refusing to replace it.");
            }

            var gameObject = api.RuntimeInvoke(_getComponentGameObject, manager, 0);
            if (gameObject == 0)
            {
                throw new InvalidOperationException("NewNetworkManager has no GameObject.");
            }
            authenticator = UnityUiRuntime.AddComponentPointer(
                gameObject,
                _basicAuthenticatorClass);
            if (authenticator == 0)
            {
                throw new InvalidOperationException("Could not add Mirror BasicAuthenticator.");
            }
            OwnedAuthenticators.Add(manager, authenticator);
        }

        var existing = api.ReadObjectReference(manager, _networkManagerAuthenticatorField);
        if (existing != 0 && existing != authenticator)
        {
            throw new InvalidOperationException(
                "NewNetworkManager authenticator ownership changed after OFS configuration.");
        }

        var profile = Profile;
        var credentials = NetworkAuthenticationProfiles.Create(profile);
        var username = api.NewString(credentials.Username);
        var password = api.NewString(credentials.Password);
        api.WriteObjectReference(authenticator, _basicServerUsernameField, username);
        api.WriteObjectReference(authenticator, _basicServerPasswordField, password);
        api.WriteObjectReference(authenticator, _basicClientUsernameField, username);
        api.WriteObjectReference(authenticator, _basicClientPasswordField, password);
        api.WriteObjectReference(manager, _networkManagerAuthenticatorField, authenticator);
        if (isServer)
        {
            _serverAuthenticatorManager = manager;
        }

        SafeLog(
            $"OFS dedicated Mirror authenticator configured: role={(isServer ? "server" : "client")}, " +
            $"manager=0x{manager:X}, component=0x{authenticator:X}, " +
            $"timeoutMs={AuthenticationTimeoutMilliseconds}.");
    }

    private static void ClearDedicatedAuthenticator(nint manager)
    {
        if (manager == 0 || _api is null)
        {
            return;
        }
        if (OwnedAuthenticators.TryGetValue(manager, out var owned) &&
            _api.ReadObjectReference(manager, _networkManagerAuthenticatorField) == owned)
        {
            _api.WriteObjectReference(manager, _networkManagerAuthenticatorField, 0);
        }
        if (_serverAuthenticatorManager == manager)
        {
            _serverAuthenticatorManager = 0;
            PendingAuthentications.Clear();
        }
    }

    private static void PollAuthenticationTimeouts()
    {
        if (_api is null || PendingAuthentications.Count == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        foreach (var (connection, deadline) in PendingAuthentications.ToArray())
        {
            try
            {
                if (_api.ReadBoolean(connection, _connectionAuthenticatedField))
                {
                    PendingAuthentications.Remove(connection);
                    SafeLog(
                        $"OFS Mirror authentication state confirmed for " +
                        $"'{ReadConnectionAddress(connection)}'.");
                    continue;
                }
                if (now < deadline)
                {
                    continue;
                }

                PendingAuthentications.Remove(connection);
                DisconnectPeer(connection, new NetworkCompatibilityResult(
                    NetworkCompatibilityStatus.AuthenticationTimeout,
                    false,
                    "Peer did not complete the OFS Mirror authentication handshake before timeout."));
            }
            catch (Exception exception)
            {
                PendingAuthentications.Remove(connection);
                SafeLog($"OFS authentication timeout tracking discarded stale peer: {exception.Message}");
            }
        }
    }

    private static unsafe void PublishMemberProfile(nint manager)
    {
        var api = RequireApi();
        var profile = Profile;
        WriteMemberMetadata(api, manager, MemberProtocolKey, profile.ProtocolVersion.ToString());
        WriteMemberMetadata(api, manager, MemberProfileKey, profile.Fingerprint);
        WriteMemberMetadata(
            api,
            manager,
            MemberModsKey,
            NetworkProfileMetadata.EncodeRequiredMods(profile.RequiredMods));
        SafeLog($"OFS member profile published: fingerprint={profile.Fingerprint}.");
    }

    private static void BlockNetworking(NetworkCompatibilityResult result)
    {
        Volatile.Write(ref _lastCheck, result);
        SafeLog($"OFS multiplayer start blocked: {result.Status}: {result.Message}");
        TryLeaveLobbyAndShowMismatch(result);
    }

    private static void LogModDifferences(
        string remoteName,
        NetworkCompatibilityResult result)
    {
        if (result.ModDifferences.Count == 0) return;
        foreach (var difference in result.ModDifferences)
        {
            SafeLog(
                $"OFS {remoteName} mod difference: kind={difference.Kind}, " +
                $"id={difference.Id}, local={difference.LocalVersion ?? "<missing>"}, " +
                $"remote={difference.RemoteVersion ?? "<missing>"}.");
        }
    }

    private static void UpdateClientRemediationPlan(NetworkCompatibilityResult result)
    {
        if (result.Allowed ||
            (result.ModDifferences.Count == 0 && result.RemoteRequiredMods.Count == 0))
        {
            Volatile.Write(ref _lastRemediationPlan, null);
            return;
        }

        try
        {
            var installedStates = ModProfileStore.GetInstalledMods(ModRuntime.LoadedMods);
            var cached = ModCatalogCache.Load();
            NetworkRemediationPlan plan;
            if (cached.Catalog is null)
            {
                plan = new NetworkRemediationPlan(
                    false,
                    false,
                    result.ModDifferences,
                    [],
                    [],
                    [],
                    [$"Trusted catalog unavailable: {cached.Status}."]);
            }
            else
            {
                plan = NetworkRemediationPlanner.Create(
                    Profile,
                    result.RemoteRequiredMods,
                    installedStates.Select(state => state.Manifest),
                    installedStates
                        .Where(state => state.ActiveEnabled)
                        .Select(state => state.Manifest.Id),
                    cached.Catalog);
            }
            Volatile.Write(ref _lastRemediationPlan, plan);
            SafeLog(
                $"OFS join remediation: success={plan.Success}, restart={plan.RestartRequired}, " +
                $"install={plan.InstallOrder.Count}, enable={plan.EnableIds.Count}, " +
                $"disable={plan.DisableIds.Count}, errors={plan.Errors.Count}.");
            foreach (var error in plan.Errors)
            {
                SafeLog($"OFS join remediation unavailable: {error}");
            }
            if (cached.IsOfficial &&
                plan.Success &&
                plan.RestartRequired &&
                Interlocked.CompareExchange(ref _automaticRemediationStarted, 1, 0) == 0)
            {
                SafeLog(
                    "OFS automatically staging the official-catalog mod set required by the host.");
                RuntimeCatalogInstaller.BeginRemediation(
                    plan,
                    status =>
                    {
                        SafeLog($"OFS automatic join remediation: {status}.");
                        if (string.Equals(status, "BUSY", StringComparison.Ordinal))
                        {
                            Interlocked.Exchange(ref _automaticRemediationStarted, 0);
                        }
                    });
            }
        }
        catch (Exception exception)
        {
            var plan = new NetworkRemediationPlan(
                false,
                false,
                result.ModDifferences,
                [],
                [],
                [],
                [$"Remediation planning failed: {exception.Message}"]);
            Volatile.Write(ref _lastRemediationPlan, plan);
            SafeLog($"OFS join remediation planning failed: {exception}");
        }
    }

    private static unsafe string? ReadLobbyMetadata(nint manager, string key)
    {
        var api = RequireApi();
        var keyString = api.NewString(key);
        nint* arguments = stackalloc nint[1];
        arguments[0] = keyString;
        var value = api.RuntimeInvoke(
            _getLobbyMetadata,
            GetCurrentLobbyPointer(api, manager),
            (nint)arguments);
        return value == 0 ? null : api.ReadString(value);
    }

    private static unsafe void WriteLobbyMetadata(nint manager, string key, string value)
    {
        var api = RequireApi();
        nint* arguments = stackalloc nint[2];
        arguments[0] = api.NewString(key);
        arguments[1] = api.NewString(value);
        _ = api.RuntimeInvoke(
            _setLobbyMetadata,
            GetCurrentLobbyPointer(api, manager),
            (nint)arguments);
    }

    private static unsafe void WriteMemberMetadata(
        IUnsafeIl2CppApi api,
        nint manager,
        string key,
        string value)
    {
        nint* arguments = stackalloc nint[2];
        arguments[0] = api.NewString(key);
        arguments[1] = api.NewString(value);
        _ = api.RuntimeInvoke(
            _setMemberMetadata,
            GetCurrentLobbyPointer(api, manager),
            (nint)arguments);
    }

    private static unsafe string? ReadMemberMetadata(nint manager, ulong steamId, string key)
    {
        var api = RequireApi();
        var lobby = GetCurrentLobbyPointer(api, manager);
        nint* getMemberArguments = stackalloc nint[2];
        getMemberArguments[0] = lobby;
        getMemberArguments[1] = (nint)(&steamId);
        var boxedMember = api.RuntimeInvoke(_getLobbyMember, 0, (nint)getMemberArguments);
        var member = api.Unbox(boxedMember);
        if (member == 0)
        {
            return null;
        }

        nint* getValueArguments = stackalloc nint[1];
        getValueArguments[0] = api.NewString(key);
        var value = api.RuntimeInvoke(
            _getLobbyMemberMetadata,
            member,
            (nint)getValueArguments);
        return value == 0 ? null : api.ReadString(value);
    }

    private static string ReadConnectionAddress(nint connection)
    {
        if (connection == 0)
        {
            return "<null>";
        }
        var api = RequireApi();
        var value = api.RuntimeInvoke(_getConnectionAddress, connection, 0);
        return value == 0 ? string.Empty : api.ReadString(value);
    }

    private static bool TryParseSteamId(string address, out ulong steamId)
    {
        if (ulong.TryParse(address, out steamId))
        {
            return true;
        }

        var end = address.Length - 1;
        while (end >= 0 && !char.IsAsciiDigit(address[end]))
        {
            end--;
        }
        if (end < 0)
        {
            steamId = 0;
            return false;
        }
        var start = end;
        while (start > 0 && char.IsAsciiDigit(address[start - 1]))
        {
            start--;
        }
        return ulong.TryParse(address.AsSpan(start, end - start + 1), out steamId);
    }

    private static unsafe void CallOriginalServerConnect(
        nint instance,
        nint connection,
        nint methodInfo)
    {
        var original = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_serverConnectOriginal;
        original(instance, connection, methodInfo);
    }

    private static void DisconnectPeer(nint connection, NetworkCompatibilityResult result)
    {
        PendingAuthentications.Remove(connection);
        Volatile.Write(ref _lastCheck, result);
        SafeLog($"OFS server rejected peer: {result.Status}: {result.Message}");
        try
        {
            if (connection != 0)
            {
                _ = RequireApi().RuntimeInvoke(_disconnectConnection, connection, 0);
            }
        }
        catch (Exception exception)
        {
            SafeLog($"OFS could not disconnect rejected peer: {exception.Message}");
        }
    }

    private static void TryLeaveLobbyAndShowMismatch(NetworkCompatibilityResult result)
    {
        try
        {
            var api = RequireApi();
            var manager = GetSteamLobbyManager();
            if (manager != 0 && ReadBoolean(_getIsInLobby, manager))
            {
                _ = api.RuntimeInvoke(_leaveLobby, manager, 0);
            }

            _ = api.RuntimeInvoke(_hideAllLoadingsImmediate, 0, 0);
            var mainMenu = api.RuntimeInvoke(_getMainMenuManagerInstance, 0, 0);
            if (mainMenu == 0)
            {
                return;
            }

            unsafe
            {
                nint* arguments = stackalloc nint[2];
                arguments[0] = api.NewString($"OFS {ShortFingerprint(Profile.Fingerprint)}");
                arguments[1] = api.NewString(
                    result.HostFingerprint is null
                        ? "OFS profile required"
                        : $"OFS {ShortFingerprint(result.HostFingerprint)}");
                _ = api.RuntimeInvoke(_showVersionMismatchPopup, mainMenu, (nint)arguments);
            }
        }
        catch (Exception exception)
        {
            SafeLog($"OFS could not restore the multiplayer UI after rejection: {exception.Message}");
        }
    }

    private static nint GetSteamLobbyManager() =>
        RequireApi().RuntimeInvoke(_getSteamLobbyManagerInstance, 0, 0);

    private static nint GetCurrentLobbyPointer(IUnsafeIl2CppApi api, nint manager) =>
        manager + api.GetFieldOffset(_currentLobbyField);

    private static bool ReadBoolean(nint getter, nint instance)
    {
        var api = RequireApi();
        var boxed = api.RuntimeInvoke(getter, instance, 0);
        var value = api.Unbox(boxed);
        return value != 0 && Marshal.ReadByte(value) != 0;
    }

    private static string ReadGameFingerprint(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "OFS", "install-manifest.json");
        if (!File.Exists(path))
        {
            return "unverified-game-build";
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("gameFingerprint", out var value) &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : "unverified-game-build";
    }

    private static IUnsafeIl2CppApi RequireApi() => _api
        ?? throw new InvalidOperationException("OFS network compatibility runtime is not configured.");

    private static nint RequireClass(
        IUnsafeIl2CppApi api,
        string assembly,
        string namespaze,
        string name)
    {
        var klass = api.FindClass(assembly, namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"IL2CPP class '{namespaze}.{name}' was not found.");
    }

    private static nint RequireMethod(IUnsafeIl2CppApi api, nint klass, string name, int arguments)
    {
        var method = api.FindMethod(klass, name, arguments);
        return method != 0
            ? method
            : throw new MissingMethodException($"IL2CPP method '{name}/{arguments}' was not found.");
    }

    private static nint RequireField(IUnsafeIl2CppApi api, nint klass, string name)
    {
        var field = api.FindField(klass, name);
        return field != 0
            ? field
            : throw new MissingFieldException($"IL2CPP field '{name}' was not found.");
    }

    private static nint RequireMethodPointer(IUnsafeIl2CppApi api, nint method)
    {
        var pointer = api.GetMethodPointer(method);
        return pointer != 0
            ? pointer
            : throw new MissingMethodException("IL2CPP method has no native implementation pointer.");
    }

    private static string ShortFingerprint(string value) =>
        value.Length <= 12 ? value : value[..12];

    private static void SafeLog(string message)
    {
        try
        {
            RuntimeLog.Write(message);
        }
        catch
        {
            // Never permit diagnostics to escape an unmanaged hook boundary.
        }
    }
}
