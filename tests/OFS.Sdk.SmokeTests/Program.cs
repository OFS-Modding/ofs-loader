using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using OFS.Loader;
using OFS.Runtime.Entry;
using OFS.Sdk;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] ReadSharedFile(string path)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    var bytes = new byte[checked((int)stream.Length)];
    stream.ReadExactly(bytes);
    return bytes;
}

static byte[] BuildWave(
    ushort format,
    ushort channels,
    int frequency,
    ushort bitsPerSample,
    byte[] sampleBytes,
    bool includeOddJunk = false,
    bool extensible = false)
{
    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(0);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    if (includeOddJunk)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes("JUNK"));
        writer.Write(1);
        writer.Write((byte)0x7f);
        writer.Write((byte)0);
    }
    writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
    writer.Write(extensible ? 40 : 16);
    writer.Write(extensible ? (ushort)0xfffe : format);
    writer.Write(channels);
    writer.Write(frequency);
    var blockAlign = checked((ushort)(channels * (bitsPerSample / 8)));
    writer.Write(checked(frequency * blockAlign));
    writer.Write(blockAlign);
    writer.Write(bitsPerSample);
    if (extensible)
    {
        writer.Write((ushort)22);
        writer.Write(bitsPerSample);
        writer.Write(0u);
        writer.Write((uint)format);
        writer.Write((ushort)0);
        writer.Write((ushort)0x0010);
        writer.Write(new byte[] { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 });
    }
    writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    writer.Write(sampleBytes.Length);
    writer.Write(sampleBytes);
    if ((sampleBytes.Length & 1) != 0) writer.Write((byte)0);
    writer.Flush();
    output.Position = 4;
    writer.Write(checked((int)output.Length - 8));
    writer.Flush();
    return output.ToArray();
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

Assert(ModVersion.TryParse("1.2.3", out var version) && version.ToString() == "1.2.3",
    "Stable version parsing failed.");
Assert(!ModVersion.TryParse("1.2", out _), "Two-component version was accepted.");
Assert(!ModVersion.TryParse("01.2.3", out _), "Leading-zero version was accepted.");
Assert(ModVersionRange.TryParse(">=1.0.0,<2.0.0", out var range) &&
       range!.Contains(new ModVersion(1, 5, 0)) &&
       !range.Contains(new ModVersion(2, 0, 0)),
    "Version range evaluation failed.");
Assert(UnityVector2.Zero == new UnityVector2(0f, 0f) &&
       UnityVector2.One == new UnityVector2(1f, 1f),
    "UnityVector2 constants are invalid.");
Assert(UnityVector4.Zero == new UnityVector4(0f, 0f, 0f, 0f) &&
       UnityVector4.One == new UnityVector4(1f, 1f, 1f, 1f),
    "UnityVector4 constants are invalid.");
Assert((int)ModMeshTopology.Triangles == 0 && (int)ModMeshTopology.Quads == 2 &&
       (int)ModMeshTopology.Lines == 3 && (int)ModMeshTopology.LineStrip == 4 &&
       (int)ModMeshTopology.Points == 5,
    "Runtime mesh topology values diverged from Unity.");
var validMeshGeometry = new ModMeshGeometry(
    Vertices: [new(0f, 1f, 0f), new(-1f, -1f, 0f), new(1f, -1f, 0f)],
    SubMeshes: [new([0, 1, 2])],
    Uv0: [new(0.5f, 1f), new(0f, 0f), new(1f, 0f)]);
ModAssets.ValidateMeshGeometryForTests(validMeshGeometry);
AssertThrows<ArgumentException>(
    () => ModAssets.ValidateMeshGeometryForTests(validMeshGeometry with
    {
        SubMeshes = [new ModSubMeshDefinition([0, 1])],
    }),
    "Runtime mesh accepted an incomplete triangle.");
AssertThrows<ArgumentOutOfRangeException>(
    () => ModAssets.ValidateMeshGeometryForTests(validMeshGeometry with
    {
        SubMeshes = [new ModSubMeshDefinition([0, 1, 3])],
    }),
    "Runtime mesh accepted an out-of-range vertex index.");
AssertThrows<ArgumentException>(
    () => ModAssets.ValidateMeshGeometryForTests(validMeshGeometry with
    {
        Uv0 = [new UnityVector2(0f, 0f)],
    }),
    "Runtime mesh accepted a partial vertex channel.");
AssertThrows<ArgumentOutOfRangeException>(
    () => ModAssets.ValidateMeshGeometryForTests(validMeshGeometry with
    {
        Vertices = [new UnityVector3(float.NaN, 0f, 0f), new(0f, 0f, 0f), new(1f, 0f, 0f)],
    }),
    "Runtime mesh accepted non-finite geometry.");
AssertThrows<ArgumentException>(
    () => ModAssets.ValidateMeshGeometryForTests(validMeshGeometry with
    {
        Uv0 = null,
        RecalculateTangents = true,
    }),
    "Runtime mesh accepted tangent recalculation without UV0.");
Assert((int)ModForceMode.Force == 0 && (int)ModForceMode.Impulse == 1 &&
       (int)ModForceMode.VelocityChange == 2 && (int)ModForceMode.Acceleration == 5,
    "ForceMode values diverged from Unity.");
Assert((int)ModRigidbodyConstraints.FreezeAll == 126 &&
       (int)ModRigidbodyConstraints.FreezePosition == 14 &&
       (int)ModRigidbodyConstraints.FreezeRotation == 112,
    "RigidbodyConstraints values diverged from Unity.");
PhysicsApi.ValidateColliderDefinitionForTests(new ModBoxColliderDefinition());
PhysicsApi.ValidateColliderDefinitionForTests(new ModSphereColliderDefinition());
PhysicsApi.ValidateColliderDefinitionForTests(new ModCapsuleColliderDefinition());
PhysicsApi.ValidateRigidbodyDefinition(new ModRigidbodyDefinition());
AssertThrows<ArgumentOutOfRangeException>(
    () => PhysicsApi.ValidateColliderDefinitionForTests(
        new ModSphereColliderDefinition(Radius: 0f, Center: UnityVector3.Zero)),
    "Runtime physics accepted a zero-radius sphere.");
AssertThrows<ArgumentOutOfRangeException>(
    () => PhysicsApi.ValidateColliderDefinitionForTests(
        new ModBoxColliderDefinition(UnityVector3.Zero, new UnityVector3(float.NaN, 1f, 1f))),
    "Runtime physics accepted non-finite collider geometry.");
AssertThrows<ArgumentException>(
    () => PhysicsApi.ValidateColliderDefinitionForTests(
        new ModMeshColliderDefinition(new UnityObject(1), Convex: false, IsTrigger: true)),
    "Runtime physics accepted a concave trigger MeshCollider.");
AssertThrows<ArgumentOutOfRangeException>(
    () => PhysicsApi.ValidateRigidbodyDefinition(new ModRigidbodyDefinition(Mass: 0f)),
    "Runtime physics accepted a zero-mass Rigidbody.");
AssertThrows<ArgumentOutOfRangeException>(
    () => PhysicsApi.ValidateRigidbodyDefinition(
        new ModRigidbodyDefinition(Constraints: (ModRigidbodyConstraints)int.MaxValue)),
    "Runtime physics accepted unknown Rigidbody constraints.");
ModMessageBus.ValidateTopicForTests("provider.state/ready");
AssertThrows<ArgumentException>(
    () => ModMessageBus.ValidateTopicForTests("bad topic"),
    "Local message bus accepted whitespace in a topic.");
AssertThrows<ArgumentException>(
    () => ModMessageBus.ValidateTopicForTests(new string('a', ModMessageBusLimits.MaximumTopicLength + 1)),
    "Local message bus accepted an oversized topic.");

var baseMod = Manifest("test.base", "1.5.0");
var rootMod = Manifest(
    "test.root",
    "2.0.0",
    [new ModDependency { Id = "test.base", Version = ">=1.0.0,<2.0.0" }]);

var enabled = ModProfileResolver.Enable([baseMod, rootMod], [], "test.root");
Assert(enabled.Success, "Profile enable resolution failed.");
Assert(enabled.EnabledIds.SequenceEqual(["test.base", "test.root"]),
    "Required dependency was not enabled before the root.");

var disabled = ModProfileResolver.Disable(
    [baseMod, rootMod],
    ["test.base", "test.root"],
    "test.base");
Assert(disabled.Success && disabled.EnabledIds.Count == 0,
    "Disabling a dependency did not disable its dependent.");
Assert(disabled.AffectedIds.SequenceEqual(["test.base", "test.root"]),
    "Disable cascade did not report both affected mods.");

var missing = ModProfileResolver.Enable([rootMod], [], "test.root");
Assert(!missing.Success && missing.Errors.Any(error => error.Contains("missing", StringComparison.Ordinal)),
    "Missing dependency was not rejected.");

var cycleA = Manifest(
    "test.cycle-a",
    "1.0.0",
    [new ModDependency { Id = "test.cycle-b" }]);
var cycleB = Manifest(
    "test.cycle-b",
    "1.0.0",
    [new ModDependency { Id = "test.cycle-a" }]);
var cycle = ModProfileResolver.Enable([cycleA, cycleB], [], "test.cycle-a");
Assert(!cycle.Success && cycle.Errors.Any(error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase)),
    "Dependency cycle was not rejected.");

using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var catalog = new ModCatalog
{
    GeneratedAtUtc = DateTimeOffset.Parse("2026-07-19T00:00:00Z"),
    GameBuild = "test-build",
    FrameworkVersion = "0.1.0",
    Mods =
    [
        new ModCatalogEntry
        {
            Id = "test.signed",
            Name = "Signed Test",
            Version = "1.0.0",
            SdkVersion = "0.1.0",
            GameBuilds = ["test-build"],
            Package = new ModCatalogPackage
            {
                Url = "https://example.invalid/test.ofmod",
                Bytes = 1,
                Sha256 = new string('0', 64),
            },
        },
    ],
};
var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
var envelope = ModCatalogSignatures.Sign(catalogBytes, "test.catalog.2026", signingKey);
var trustStore = new ModCatalogTrustStore
{
    Keys = [ModCatalogSignatures.ExportTrustKey("test.catalog.2026", signingKey)],
};
var verified = ModCatalogSignatures.Verify(envelope, trustStore);
Assert(verified.Success && verified.Catalog?.Mods.Single().Id == "test.signed",
    "Signed catalog verification failed.");

var signature = Convert.FromBase64String(envelope.Signature);
signature[0] ^= 0x80;
var tampered = envelope with { Signature = Convert.ToBase64String(signature) };
Assert(!ModCatalogSignatures.Verify(tampered, trustStore).Success,
    "Tampered catalog signature was accepted.");
Assert(!ModCatalogSignatures.Verify(
        envelope,
        new ModCatalogTrustStore { Keys = [] }).Success,
    "Catalog signed by an unknown key was accepted.");
var officialCatalogKey = OfficialCatalogIdentity.CreateTrustKey();
Assert(
    officialCatalogKey.Id == OfficialCatalogIdentity.KeyId &&
    officialCatalogKey.Sha256 == OfficialCatalogIdentity.PublicKeySha256 &&
    OfficialCatalogIdentity.Source.StartsWith("https://", StringComparison.Ordinal),
    "Official catalog identity is not internally consistent.");

var thumbnailBytes = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nKsAAAAASUVORK5CYII=");
var thumbnail = new ModCatalogThumbnail
{
    Url = "https://example.invalid/thumbnail.png",
    Bytes = thumbnailBytes.Length,
    Sha256 = Convert.ToHexString(SHA256.HashData(thumbnailBytes)).ToLowerInvariant(),
};
var thumbnailCatalog = catalog with
{
    Mods = [catalog.Mods.Single() with { Thumbnail = thumbnail }],
};
Assert(ModCatalogValidator.Validate(thumbnailCatalog).Count == 0,
    "Valid signed thumbnail metadata was rejected.");
Assert(ModCatalogValidator.Validate(thumbnailCatalog with
{
    Mods = [thumbnailCatalog.Mods.Single() with
    {
        Thumbnail = thumbnail with { Url = "http://example.invalid/thumbnail.png" },
    }],
}).Any(error => error.Contains("thumbnail URL", StringComparison.Ordinal)),
    "Non-HTTPS thumbnail URL was accepted.");
Assert(ModCatalogValidator.Validate(thumbnailCatalog with
{
    Mods = [thumbnailCatalog.Mods.Single() with
    {
        Thumbnail = thumbnail with { Bytes = ModCatalogValidator.MaximumThumbnailBytes + 1 },
    }],
}).Any(error => error.Contains("thumbnail size", StringComparison.Ordinal)),
    "Oversized thumbnail metadata was accepted.");

var inspectedThumbnail = CatalogThumbnailStore.Inspect(thumbnailBytes);
Assert(inspectedThumbnail.Format == CatalogThumbnailFormat.Png &&
       inspectedThumbnail.Width == 1 && inspectedThumbnail.Height == 1,
    "PNG thumbnail inspection failed.");
var inspectedLooseImage = CatalogThumbnailStore.InspectRaster(
    thumbnailBytes,
    ModImageLimits.MaximumDimension,
    ModImageLimits.MaximumPixels,
    "Mod image");
Assert(inspectedLooseImage.Format == CatalogThumbnailFormat.Png &&
       inspectedLooseImage.Width == 1 && inspectedLooseImage.Height == 1,
    "Loose PNG image inspection failed.");
AssertThrows<ArgumentOutOfRangeException>(
    () => CatalogThumbnailStore.InspectRaster(thumbnailBytes, 0, 1),
    "A non-positive loose-image dimension limit was accepted.");
AssertThrows<ArgumentOutOfRangeException>(
    () => CatalogThumbnailStore.InspectRaster(thumbnailBytes, 1, 0),
    "A non-positive loose-image pixel limit was accepted.");
byte[] jpegThumbnail =
[
    0xFF, 0xD8,
    0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20,
    0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
];
var inspectedJpeg = CatalogThumbnailStore.Inspect(jpegThumbnail);
Assert(inspectedJpeg.Format == CatalogThumbnailFormat.Jpeg &&
       inspectedJpeg.Width == 32 && inspectedJpeg.Height == 16,
    "JPEG thumbnail inspection failed.");
var oversizedPng = thumbnailBytes.ToArray();
oversizedPng[16] = 0;
oversizedPng[17] = 0;
oversizedPng[18] = 8;
oversizedPng[19] = 0;
AssertThrows<InvalidDataException>(
    () => CatalogThumbnailStore.Inspect(oversizedPng),
    "Oversized thumbnail dimensions were accepted.");

var pcm16Wave = BuildWave(
    1,
    1,
    8_000,
    16,
    [0x00, 0x80, 0x00, 0x00, 0xff, 0x7f],
    includeOddJunk: true);
var decodedPcm16 = WaveAudioDecoder.Decode(pcm16Wave);
Assert(decodedPcm16.Encoding == ModWaveEncoding.PcmInteger &&
       decodedPcm16.Channels == 1 && decodedPcm16.Frequency == 8_000 &&
       decodedPcm16.BitsPerSample == 16 && decodedPcm16.SampleFrames == 3 &&
       decodedPcm16.Samples.Length == 3 && decodedPcm16.Samples[0] == -1f &&
       decodedPcm16.Samples[1] == 0f && decodedPcm16.Samples[2] > 0.999f,
    "16-bit PCM WAV decoding or odd-chunk traversal failed.");
var decodedPcm8 = WaveAudioDecoder.Decode(BuildWave(1, 1, 8_000, 8, [0, 128, 255]));
Assert(decodedPcm8.Samples[0] == -1f && decodedPcm8.Samples[1] == 0f &&
       decodedPcm8.Samples[2] > 0.99f,
    "8-bit PCM WAV decoding failed.");
var decodedPcm24 = WaveAudioDecoder.Decode(BuildWave(
    1,
    1,
    8_000,
    24,
    [0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0xff, 0xff, 0x7f],
    extensible: true));
Assert(decodedPcm24.Samples[0] == -1f && decodedPcm24.Samples[1] == 0f &&
       decodedPcm24.Samples[2] > 0.999f,
    "24-bit extensible PCM WAV decoding failed.");
var decodedPcm32 = WaveAudioDecoder.Decode(BuildWave(
    1,
    1,
    8_000,
    32,
    [0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0x7f]));
Assert(decodedPcm32.Samples[0] == -1f && decodedPcm32.Samples[1] == 0f &&
       decodedPcm32.Samples[2] > 0.999f,
    "32-bit PCM WAV decoding failed.");
var floatSamples = new[] { -2f, 0.25f, 2f };
var floatBytes = new byte[floatSamples.Length * sizeof(float)];
Buffer.BlockCopy(floatSamples, 0, floatBytes, 0, floatBytes.Length);
var decodedFloat = WaveAudioDecoder.Decode(BuildWave(3, 1, 48_000, 32, floatBytes));
Assert(decodedFloat.Encoding == ModWaveEncoding.IeeeFloat &&
       decodedFloat.Samples.SequenceEqual([-1f, 0.25f, 1f]),
    "IEEE-float WAV decoding/clamping failed.");
var nonFiniteBytes = new byte[sizeof(float)];
Buffer.BlockCopy(new[] { float.NaN }, 0, nonFiniteBytes, 0, nonFiniteBytes.Length);
AssertThrows<InvalidDataException>(
    () => WaveAudioDecoder.Decode(BuildWave(3, 1, 8_000, 32, nonFiniteBytes)),
    "A WAV containing a non-finite sample was accepted.");
var malformedWave = pcm16Wave.ToArray();
var formatOffset = 30;
malformedWave[formatOffset + 12] = 4;
AssertThrows<InvalidDataException>(
    () => WaveAudioDecoder.Decode(malformedWave),
    "A WAV with inconsistent block alignment was accepted.");

var thumbnailRoot = Path.Combine(
    Path.GetTempPath(),
    "ofs-sdk-thumbnail-smoke",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(thumbnailRoot);
try
{
    var handler = new ThumbnailHttpHandler(thumbnailBytes);
    using var thumbnailHttp = new HttpClient(handler);
    var thumbnailStore = new CatalogThumbnailStore(thumbnailRoot, thumbnailHttp);
    var fetchedThumbnail = await thumbnailStore.GetOrFetchAsync(thumbnail);
    Assert(!fetchedThumbnail.FromCache && handler.RequestCount == 1 &&
           File.ReadAllBytes(fetchedThumbnail.Path).SequenceEqual(thumbnailBytes),
        "Thumbnail was not downloaded into the verified content-addressed cache.");
    var cachedThumbnail = await new CatalogThumbnailStore(thumbnailRoot, thumbnailHttp)
        .GetOrFetchAsync(thumbnail);
    Assert(cachedThumbnail.FromCache && handler.RequestCount == 1,
        "Verified thumbnail cache hit performed an unnecessary network request.");

    var corruptBytes = thumbnailBytes.ToArray();
    corruptBytes[^1] ^= 0x80;
    await File.WriteAllBytesAsync(cachedThumbnail.Path, corruptBytes);
    var repairedThumbnail = await new CatalogThumbnailStore(thumbnailRoot, thumbnailHttp)
        .GetOrFetchAsync(thumbnail);
    Assert(!repairedThumbnail.FromCache && handler.RequestCount == 2 &&
           File.ReadAllBytes(repairedThumbnail.Path).SequenceEqual(thumbnailBytes),
        "Corrupt content-addressed thumbnail cache was not replaced.");

    var badHashRoot = Path.Combine(thumbnailRoot, "bad-hash");
    var badHashStore = new CatalogThumbnailStore(badHashRoot, thumbnailHttp);
    await AssertThrowsAsync<InvalidDataException>(
        () => badHashStore.GetOrFetchAsync(thumbnail with { Sha256 = new string('0', 64) }),
        "Thumbnail with an incorrect signed hash was accepted.");
    Assert(!Directory.EnumerateFiles(
            badHashRoot,
            "*.tmp-*",
            SearchOption.TopDirectoryOnly).Any(),
        "Failed thumbnail download leaked a staging file.");
}
finally
{
    Directory.Delete(thumbnailRoot, recursive: true);
}

var browser = new ModCatalogBrowser(
[
    CatalogEntry("test.alpha", "Alpha Logistics", "Conveyor tools", "Ada", ["factory.logistics"]),
    CatalogEntry("test.beta", "Beta Citizens", "Adds workers", "Bruno", ["npc.workers"]),
    CatalogEntry("test.gamma", "Gamma Colors", "Ore palettes", "Carla", ["visual.ores"]),
    CatalogEntry("test.delta", "Delta Factory", "Advanced machines", "Diego", ["factory.machines"]),
    CatalogEntry("test.epsilon", "Epsilon UI", "Dashboards", "Elena", ["ui.overlay"]),
]);
Assert(browser.PageCount == 3 &&
       browser.PageEntries.Select(entry => entry.Id).SequenceEqual(["test.alpha", "test.beta"]),
    "Catalog browser did not create deterministic two-card pages.");
Assert(browser.NextPage() && browser.PageIndex == 1 &&
       browser.PageEntries.Select(entry => entry.Id).SequenceEqual(["test.gamma", "test.delta"]),
    "Catalog browser could not advance to its second page.");
browser.SetQuery("factory");
Assert(browser.PageIndex == 0 && browser.PageCount == 1 && browser.MatchCount == 2 &&
       browser.PageEntries.Select(entry => entry.Id).SequenceEqual(["test.alpha", "test.delta"]),
    "Catalog search did not cover summaries/capabilities or reset pagination.");
Assert(browser.Select("TEST.DELTA") && browser.Selected?.Author == "Diego",
    "Catalog detail selection was not ID-case-insensitive.");
browser.BackToResults();
browser.SetQuery("carla");
Assert(browser.MatchCount == 1 && browser.PageEntries.Single().Id == "test.gamma",
    "Catalog search did not cover author metadata.");
browser.SetQuery("missing");
Assert(browser.MatchCount == 0 && browser.PageCount == 1 && browser.PageEntries.Count == 0 &&
       !browser.NextPage() && !browser.PreviousPage(),
    "Empty catalog search produced invalid pagination.");

var networkProfile = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [
        new NetworkModIdentity("test.client", "1.0.0", "client"),
        new NetworkModIdentity("test.required", "2.0.0", "required"),
    ]);
Assert(networkProfile.RequiredMods.Select(mod => mod.Id).SequenceEqual(["test.required"]),
    "Client-only mod leaked into required multiplayer fingerprint.");
Assert(networkProfile.ProtocolVersion == NetworkCompatibilityProfiles.CurrentProtocolVersion &&
       networkProfile.ProtocolVersion == 2,
    "Network compatibility profile did not move to canonical metadata protocol v2.");
Assert(!NetworkCompatibilityRuntime.ShouldBlockHostStart(
           blocksMultiplayer: true,
           singlePlayer: true) &&
       NetworkCompatibilityRuntime.ShouldBlockHostStart(
           blocksMultiplayer: true,
           singlePlayer: false) &&
       !NetworkCompatibilityRuntime.ShouldBlockHostStart(
           blocksMultiplayer: false,
           singlePlayer: false),
    "Host compatibility policy did not exempt the local single-player Mirror host.");
var networkProfileReordered = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [
        new NetworkModIdentity("test.required", "2.0.0", "required"),
        new NetworkModIdentity("test.client", "1.0.0", "client"),
    ]);
Assert(networkProfileReordered.Fingerprint == networkProfile.Fingerprint,
    "Network profile fingerprint still depends on mod discovery order.");
var requiredMetadata = NetworkProfileMetadata.EncodeRequiredMods(networkProfile.RequiredMods);
Assert(requiredMetadata == "test.required@2.0.0" &&
       NetworkProfileMetadata.TryDecodeRequiredMods(
           requiredMetadata, out var decodedRequired, out var requiredMetadataError) &&
       requiredMetadataError.Length == 0 &&
       decodedRequired.SequenceEqual(networkProfile.RequiredMods),
    "Required-mod Steam metadata did not round-trip canonically.");
Assert(!NetworkProfileMetadata.TryDecodeRequiredMods(
        "test.required@2.0.0,TEST.REQUIRED@2.0.0",
        out _,
        out var duplicateMetadataError) &&
       duplicateMetadataError.Contains("duplicate", StringComparison.OrdinalIgnoreCase),
    "Required-mod metadata accepted duplicate ids.");
AssertThrows<ArgumentException>(
    () => NetworkProfileMetadata.EncodeRequiredMods(
        [new NetworkModIdentity("test.client", "1.0.0", "client")]),
    "Required-mod metadata accepted a client-only identity.");
Assert(NetworkCompatibilityProfiles.CompareHost(
        networkProfile,
        NetworkCompatibilityProfiles.CurrentProtocolVersion.ToString(),
        networkProfile.Fingerprint).Allowed,
    "Identical multiplayer profile was rejected.");
Assert(!NetworkCompatibilityProfiles.CompareHost(
        networkProfile,
        NetworkCompatibilityProfiles.CurrentProtocolVersion.ToString(),
        new string('0', 64)).Allowed,
    "Different multiplayer fingerprint was accepted.");
Assert(!NetworkCompatibilityProfiles.CompareHost(
        networkProfile,
        "999",
        networkProfile.Fingerprint).Allowed,
    "Unsupported multiplayer profile protocol was accepted.");
Assert(!NetworkCompatibilityProfiles.CompareHost(networkProfile, null, null).Allowed,
    "Legacy host was accepted while required mods are active.");
var detailedMismatch = NetworkCompatibilityProfiles.CompareHost(
    networkProfile,
    NetworkCompatibilityProfiles.CurrentProtocolVersion.ToString(),
    new string('0', 64),
    "test.other@1.0.0,test.required@3.0.0");
Assert(!detailedMismatch.Allowed &&
       detailedMismatch.ModDifferences.Count == 2 &&
       detailedMismatch.ModDifferences.Any(difference =>
           difference.Kind == NetworkModDifferenceKind.MissingLocal &&
           difference.Id == "test.other") &&
       detailedMismatch.ModDifferences.Any(difference =>
           difference.Kind == NetworkModDifferenceKind.VersionMismatch &&
           difference.LocalVersion == "2.0.0" &&
           difference.RemoteVersion == "3.0.0") &&
       detailedMismatch.RemoteRequiredMods.Count == 2,
    "Detailed host comparison did not expose actionable missing/version differences.");
var invalidDetailedProfile = NetworkCompatibilityProfiles.CompareHost(
    networkProfile,
    NetworkCompatibilityProfiles.CurrentProtocolVersion.ToString(),
    new string('0', 64),
    "not valid metadata");
Assert(!invalidDetailedProfile.Allowed &&
       invalidDetailedProfile.Status == NetworkCompatibilityStatus.InvalidPeerProfile,
    "Malformed remote remediation metadata did not fail closed.");
var credentials = NetworkAuthenticationProfiles.Create(networkProfile);
Assert(credentials.Mode == NetworkAuthenticationProfiles.MirrorBasicMode &&
       credentials.Username.Contains(networkProfile.GameBuild, StringComparison.Ordinal) &&
       credentials.Password == networkProfile.Fingerprint,
    "Dedicated Mirror credentials do not bind protocol/build/framework/profile.");
var missingPeer = NetworkCompatibilityProfiles.ComparePeer(networkProfile, null, null);
Assert(!missingPeer.Allowed &&
       missingPeer.Status == NetworkCompatibilityStatus.MissingPeerProfile,
    "Host-side peer enforcement accepted a peer without an OFS profile.");
var clientOnlyProfile = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [new NetworkModIdentity("test.client", "1.0.0", "client")]);
Assert(NetworkCompatibilityProfiles.CompareHost(clientOnlyProfile, null, null).Allowed,
    "Legacy host was rejected for a client-only profile.");
var blockedProfile = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [new NetworkModIdentity("test.offline", "1.0.0", "incompatible")]);
Assert(!NetworkCompatibilityProfiles.CompareHost(
        blockedProfile,
        NetworkCompatibilityProfiles.CurrentProtocolVersion.ToString(),
        blockedProfile.Fingerprint).Allowed,
    "multiplayer=incompatible mod did not block multiplayer.");

var installedOld = Manifest("test.old", "1.0.0") with { Multiplayer = "required" };
var installedExtra = Manifest("test.extra", "1.0.0") with { Multiplayer = "required" };
var installedExtraChild = Manifest(
    "test.extra-child",
    "1.0.0",
    [new ModDependency { Id = "test.extra", Version = "1.0.0" }]) with
{
    Multiplayer = "client",
};
var installedOffline = Manifest("test.offline", "1.0.0") with
{
    Multiplayer = "incompatible",
};
var remediationLocalProfile = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [
        new NetworkModIdentity("test.old", "1.0.0", "required"),
        new NetworkModIdentity("test.extra", "1.0.0", "required"),
        new NetworkModIdentity("test.extra-child", "1.0.0", "client"),
        new NetworkModIdentity("test.offline", "1.0.0", "incompatible"),
    ]);
var remediationCatalog = catalog with
{
    GameBuild = "ABCDEF",
    Mods =
    [
        CatalogEntry("test.lib", "Library", "Dependency", "SDK", []) with
        {
            Multiplayer = "client",
        },
        CatalogEntry("test.new", "New", "Host mod", "SDK", []) with
        {
            Multiplayer = "required",
            Dependencies = [new ModDependency { Id = "test.lib", Version = "1.0.0" }],
        },
        CatalogEntry("test.old", "Old v2", "Upgrade", "SDK", []) with
        {
            Version = "2.0.0",
            Multiplayer = "required",
        },
    ],
};
var remoteRequired = new NetworkModIdentity[]
{
    new("test.new", "1.0.0", "required"),
    new("test.old", "2.0.0", "required"),
};
var remediationPlan = NetworkRemediationPlanner.Create(
    remediationLocalProfile,
    remoteRequired,
    [installedOld, installedExtra, installedExtraChild, installedOffline],
    ["test.old", "test.extra", "test.extra-child", "test.offline"],
    remediationCatalog);
Assert(remediationPlan.Success && remediationPlan.RestartRequired &&
       remediationPlan.Differences.Count == 3 &&
       remediationPlan.InstallOrder.Select(entry => entry.Id)
           .SequenceEqual(["test.lib", "test.new", "test.old"]) &&
       remediationPlan.EnableIds.SequenceEqual(["test.lib", "test.new"]) &&
       remediationPlan.DisableIds.SequenceEqual(
           ["test.extra", "test.extra-child", "test.offline"]),
    $"Network remediation plan was not deterministic/actionable: " +
    $"{string.Join(" ", remediationPlan.Errors)}");
var missingRemediationVersion = NetworkRemediationPlanner.Create(
    remediationLocalProfile,
    remoteRequired,
    [installedOld, installedExtra, installedExtraChild, installedOffline],
    ["test.old", "test.extra", "test.extra-child", "test.offline"],
    remediationCatalog with
    {
        Mods = remediationCatalog.Mods.Where(entry => entry.Id != "test.old").ToArray(),
    });
Assert(!missingRemediationVersion.Success &&
       missingRemediationVersion.Errors.Any(error =>
           error.Contains("No compatible", StringComparison.OrdinalIgnoreCase)),
    "Remediation planner accepted a host version absent from the catalog.");
var omittedRequiredDependency = NetworkRemediationPlanner.Create(
    remediationLocalProfile,
    remoteRequired,
    [installedOld, installedExtra, installedExtraChild, installedOffline],
    ["test.old", "test.extra", "test.extra-child", "test.offline"],
    remediationCatalog with
    {
        Mods = remediationCatalog.Mods.Select(entry =>
            entry.Id == "test.lib" ? entry with { Multiplayer = "required" } : entry).ToArray(),
    });
Assert(!omittedRequiredDependency.Success &&
       omittedRequiredDependency.Errors.Any(error =>
           error.Contains("omits required", StringComparison.OrdinalIgnoreCase)),
    "Remediation planner accepted a host profile omitting a required catalog dependency.");
var alignedOld = installedOld with { Version = "2.0.0" };
var alignedNew = Manifest(
    "test.new",
    "1.0.0",
    [new ModDependency { Id = "test.lib", Version = "1.0.0" }]) with
{
    Multiplayer = "required",
};
var alignedLib = Manifest("test.lib", "1.0.0") with { Multiplayer = "client" };
var alignedProfile = NetworkCompatibilityProfiles.Create(
    "ABCDEF",
    "0.1.0",
    [
        new NetworkModIdentity("test.new", "1.0.0", "required"),
        new NetworkModIdentity("test.old", "2.0.0", "required"),
        new NetworkModIdentity("test.lib", "1.0.0", "client"),
    ]);
var alignedPlan = NetworkRemediationPlanner.Create(
    alignedProfile,
    remoteRequired,
    [alignedOld, alignedNew, alignedLib],
    ["test.old", "test.new", "test.lib"],
    remediationCatalog);
Assert(alignedPlan.Success && !alignedPlan.RestartRequired &&
       alignedPlan.Differences.Count == 0 &&
       alignedPlan.InstallOrder.Count == 0 &&
       alignedPlan.EnableIds.Count == 0 &&
       alignedPlan.DisableIds.Count == 0,
    "Already aligned multiplayer profile produced remediation actions.");

var messagePayload = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
var messageFrame = NetworkEnvelopeCodec.Encode("test.required:factory-state", messagePayload);
Assert(NetworkEnvelopeCodec.TryDecode(
        messageFrame,
        out var decodedChannel,
        out var decodedPayload,
        out var decodeError) &&
       decodedChannel == "test.required:factory-state" &&
       decodedPayload.SequenceEqual(messagePayload) &&
       decodeError.Length == 0,
    "Mirror mod envelope did not round-trip exactly.");
var malformedFrame = messageFrame.ToArray();
malformedFrame[9] ^= 0x01;
Assert(!NetworkEnvelopeCodec.TryDecode(
        malformedFrame, out _, out _, out var malformedError) &&
       malformedError.Contains("length", StringComparison.OrdinalIgnoreCase),
    "Mirror mod envelope accepted a mismatched payload length.");
var incompatibleFrame = messageFrame.ToArray();
incompatibleFrame[6] = 2;
Assert(!NetworkEnvelopeCodec.TryDecode(incompatibleFrame, out _, out _, out _),
    "Mirror mod envelope accepted an unsupported protocol version.");
AssertThrows<ArgumentOutOfRangeException>(() => NetworkEnvelopeCodec.Encode(
    "test.required:too-large",
    new byte[NetworkEnvelopeCodec.MaxPayloadBytes + 1]),
    "Mirror mod envelope accepted an oversized payload.");
var channelContract = new ModNetworkChannelDefinition("factory-state", _ => { });
Assert(channelContract.Direction == ModNetworkDirection.Bidirectional &&
       channelContract.MaxPayloadBytes == NetworkEnvelopeCodec.MaxPayloadBytes &&
       channelContract.RequireAuthentication,
    "Network channel public defaults are not fail-closed.");

var stateValue = JsonSerializer.SerializeToUtf8Bytes(new ReplicatedProbe("ready", 7));
var stateSnapshot = ReplicatedStateCodec.EncodeSnapshot(42, stateValue);
Assert(ReplicatedStateCodec.TryDecode(
        stateSnapshot, out var decodedState, out var stateDecodeError) &&
       decodedState.Kind == ReplicatedStateMessageKind.Snapshot &&
       decodedState.Revision == 42 && decodedState.Value.SequenceEqual(stateValue) &&
       stateDecodeError.Length == 0,
    "Replicated state snapshot did not round-trip exactly.");
Assert(ReplicatedStateCodec.TryDecode(
        ReplicatedStateCodec.EncodeSyncRequest(), out var syncRequest, out _) &&
       syncRequest.Kind == ReplicatedStateMessageKind.SyncRequest,
    "Replicated state sync request did not round-trip.");
var malformedStateSnapshot = stateSnapshot.ToArray();
malformedStateSnapshot[10] ^= 0x01;
Assert(!ReplicatedStateCodec.TryDecode(
        malformedStateSnapshot, out _, out var stateLengthError) &&
       stateLengthError.Contains("length", StringComparison.OrdinalIgnoreCase),
    "Replicated state codec accepted a mismatched value length.");
var incompatibleStateSnapshot = stateSnapshot.ToArray();
incompatibleStateSnapshot[0] = 2;
Assert(!ReplicatedStateCodec.TryDecode(
        incompatibleStateSnapshot, out _, out var stateProtocolError) &&
       stateProtocolError.Contains("protocol", StringComparison.OrdinalIgnoreCase),
    "Replicated state codec accepted an unsupported protocol version.");
AssertThrows<ArgumentOutOfRangeException>(() => ReplicatedStateCodec.EncodeSnapshot(
        1, new byte[ReplicatedStateCodec.MaximumValueBytes + 1]),
    "Replicated state codec accepted an oversized value.");
var jsonStateSerializer = ModNetworkSerializers.Json<ReplicatedProbe>();
var jsonStateValue = new ReplicatedProbe("json", 11);
Assert(jsonStateSerializer.Deserialize(jsonStateSerializer.Serialize(jsonStateValue)) == jsonStateValue,
    "Public replicated-state JSON serializer did not round-trip.");
var stateContract = new ModReplicatedStateDefinition<ReplicatedProbe>(
    "factory-status", new ReplicatedProbe("idle", 0));
Assert(stateContract.Serializer is null && stateContract.MaxValueBytes == 16 * 1024 &&
       stateContract.DisableOnException,
    "Replicated state public defaults are not fail-closed.");

var stateLogger = new ModRuntime.ModLogger("test.state");
var serverStateChannel = new TestReplicatedChannel();
var serverState = new ReplicatedState<ReplicatedProbe>(
    "test.required",
    stateLogger,
    new ModReplicatedStateDefinition<ReplicatedProbe>(
        "factory-status", new ReplicatedProbe("authoritative", 9)),
    serverStateChannel,
    () => true,
    () => false,
    () => true,
    _ => { });
((IReplicatedStateRuntime)serverState).OnFrame(new FrameEvent(1, 0.016f, 0.016f), true, false);
Assert(serverState.IsSynchronized && serverState.Revision == 1,
    "Server state did not establish its initial authoritative revision.");
serverState.Receive(new ModNetworkMessageEvent(
    "state",
    "ofs.framework.state:test",
    ReplicatedStateCodec.EncodeSyncRequest(),
    ModNetworkTransport.Reliable,
    true,
    new TestNetworkPeer()));
Assert(serverStateChannel.ClientPayload is not null &&
       ReplicatedStateCodec.TryDecode(
           serverStateChannel.ClientPayload, out var serverResponse, out _) &&
       serverResponse.Kind == ReplicatedStateMessageKind.Snapshot && serverResponse.Revision == 1,
    "Server state did not answer an authenticated sync request.");

ModReplicatedStateUpdate<ReplicatedProbe>? observedStateUpdate = null;
var clientStateChannel = new TestReplicatedChannel();
var clientHandlerReady = false;
var clientState = new ReplicatedState<ReplicatedProbe>(
    "test.required",
    stateLogger,
    new ModReplicatedStateDefinition<ReplicatedProbe>(
        "factory-status",
        new ReplicatedProbe("stale", 0),
        Updated: update => observedStateUpdate = update),
    clientStateChannel,
    () => false,
    () => true,
    () => clientHandlerReady,
    _ => { });
((IReplicatedStateRuntime)clientState).OnFrame(new FrameEvent(2, 0.016f, 0.016f), false, true);
Assert(clientStateChannel.ServerPayload is null,
    "Client state sent a sync request before its Mirror handler was ready.");
clientHandlerReady = true;
((IReplicatedStateRuntime)clientState).OnFrame(new FrameEvent(3, 0.016f, 0.016f), false, true);
Assert(clientStateChannel.ServerPayload is not null &&
       ReplicatedStateCodec.TryDecode(
           clientStateChannel.ServerPayload, out var clientRequest, out _) &&
       clientRequest.Kind == ReplicatedStateMessageKind.SyncRequest,
    "Client state did not automatically request its initial snapshot.");
clientState.Receive(new ModNetworkMessageEvent(
    "state",
    "ofs.framework.state:test",
    serverStateChannel.ClientPayload!,
    ModNetworkTransport.Reliable,
    false,
    null));
Assert(clientState.IsSynchronized && clientState.Revision == 1 &&
       clientState.Value == new ReplicatedProbe("authoritative", 9) &&
       observedStateUpdate?.Origin == ModReplicatedStateUpdateOrigin.RemoteSnapshot,
    "Client state did not apply the authoritative snapshot.");
var updatesBeforeStale = observedStateUpdate;
clientState.Receive(new ModNetworkMessageEvent(
    "state",
    "ofs.framework.state:test",
    ReplicatedStateCodec.EncodeSnapshot(
        1, jsonStateSerializer.Serialize(new ReplicatedProbe("old", -1))),
    ModNetworkTransport.Unreliable,
    false,
    null));
Assert(clientState.Value == new ReplicatedProbe("authoritative", 9) &&
       observedStateUpdate == updatesBeforeStale,
    "Client state accepted a stale replicated revision.");
clientState.Dispose();
serverState.Dispose();

var rpcBody = JsonSerializer.SerializeToUtf8Bytes(new RpcProbeRequest(21));
var rpcPayload = NetworkRpcCodec.Encode(NetworkRpcMessageKind.Request, 7, rpcBody);
Assert(NetworkRpcCodec.TryDecode(
        rpcPayload, out var decodedRpc, out var rpcDecodeError) &&
       decodedRpc.Kind == NetworkRpcMessageKind.Request && decodedRpc.RequestId == 7 &&
       decodedRpc.Body.SequenceEqual(rpcBody) && rpcDecodeError.Length == 0,
    "Network RPC request did not round-trip exactly.");
var malformedRpc = rpcPayload.ToArray();
malformedRpc[6] ^= 0x01;
Assert(!NetworkRpcCodec.TryDecode(malformedRpc, out _, out var rpcLengthError) &&
       rpcLengthError.Contains("length", StringComparison.OrdinalIgnoreCase),
    "Network RPC codec accepted a mismatched body length.");
var incompatibleRpc = rpcPayload.ToArray();
incompatibleRpc[0] = 2;
Assert(!NetworkRpcCodec.TryDecode(incompatibleRpc, out _, out var rpcProtocolError) &&
       rpcProtocolError.Contains("protocol", StringComparison.OrdinalIgnoreCase),
    "Network RPC codec accepted an unsupported protocol.");
var unicodeRpcError = NetworkRpcCodec.EncodeError(9, new string('é', 20), 7);
Assert(NetworkRpcCodec.TryDecode(unicodeRpcError, out var decodedRpcError, out _) &&
       decodedRpcError.Kind == NetworkRpcMessageKind.Error &&
       NetworkRpcCodec.DecodeError(decodedRpcError.Body).Length > 0,
    "Network RPC error truncation emitted invalid UTF-8.");
AssertThrows<ArgumentOutOfRangeException>(() => NetworkRpcCodec.Encode(
        NetworkRpcMessageKind.Request,
        1,
        new byte[NetworkRpcCodec.MaximumBodyBytes + 1]),
    "Network RPC codec accepted an oversized body.");

var defaultRpcRateLimit = NetworkRpcRuntime.ResolveRateLimit(null);
Assert(defaultRpcRateLimit == ModNetworkRpcRateLimit.Default &&
       defaultRpcRateLimit.Enabled &&
       !ModNetworkRpcRateLimit.Unlimited.Enabled,
    "Network RPC rate-limit defaults are not fail-safe or explicitly disableable.");
AssertThrows<ArgumentOutOfRangeException>(() => NetworkRpcRuntime.ResolveRateLimit(
        new ModNetworkRpcRateLimit(0, 1)),
    "Network RPC accepted a zero-sized rate-limit burst.");
AssertThrows<ArgumentOutOfRangeException>(() => NetworkRpcRuntime.ResolveRateLimit(
        new ModNetworkRpcRateLimit(1, double.NaN)),
    "Network RPC accepted a non-finite refill rate.");
var deterministicLimiter = new NetworkRpcRateLimiter(
    new ModNetworkRpcRateLimit(Burst: 2, RefillPerSecond: 1),
    frequency: 1_000);
Assert(deterministicLimiter.TryConsume(11, 0) &&
       deterministicLimiter.TryConsume(11, 0) &&
       !deterministicLimiter.TryConsume(11, 0),
    "RPC token bucket did not enforce its initial per-peer burst.");
Assert(!deterministicLimiter.TryConsume(11, 999) &&
       deterministicLimiter.TryConsume(11, 1_000) &&
       deterministicLimiter.TryConsume(22, 0),
    "RPC token bucket did not refill deterministically or isolate peers.");
deterministicLimiter.RemoveIdle(301_000);
Assert(deterministicLimiter.BucketCount == 0,
    "RPC token bucket did not discard idle peer state.");

var policyChannel = new TestReplicatedChannel();
var policyHandlerCalls = 0;
var policyRpc = new NetworkRpc<RpcProbeRequest, RpcProbeResponse>(
    "test.required",
    new ModRuntime.ModLogger("test.rpc-policy"),
    new ModNetworkRpcDefinition<RpcProbeRequest, RpcProbeResponse>(
        "policy",
        request =>
        {
            ++policyHandlerCalls;
            return new RpcProbeResponse(request.Value.Value * 2);
        },
        MaxRequestBytes: 512,
        MaxResponseBytes: 512,
        Authorize: request => request.Value.Value == 13
            ? ModNetworkRpcAuthorizationResult.Deny("test authorization denied")
            : ModNetworkRpcAuthorizationResult.Allow(),
        RateLimit: new ModNetworkRpcRateLimit(Burst: 1, RefillPerSecond: 0.01)),
    policyChannel,
    () => true,
    () => true,
    () => true,
    _ => { });
var policySerializer = ModNetworkSerializers.Json<RpcProbeRequest>();
var policyPeer = new TestNetworkPeer();
policyRpc.Receive(new ModNetworkMessageEvent(
    "rpc", "ofs.framework.rpc:policy",
    NetworkRpcCodec.Encode(
        NetworkRpcMessageKind.Request, 101, policySerializer.Serialize(new RpcProbeRequest(13))),
    ModNetworkTransport.Reliable, true, policyPeer));
Assert(NetworkRpcCodec.TryDecode(policyChannel.ClientPayload!, out var deniedPolicyMessage, out _) &&
       deniedPolicyMessage.Kind == NetworkRpcMessageKind.Error &&
       NetworkRpcCodec.DecodeError(deniedPolicyMessage.Body) == "test authorization denied" &&
       policyHandlerCalls == 0,
    "RPC authorization did not fail closed before invoking the handler.");
policyRpc.Receive(new ModNetworkMessageEvent(
    "rpc", "ofs.framework.rpc:policy",
    NetworkRpcCodec.Encode(
        NetworkRpcMessageKind.Request, 102, policySerializer.Serialize(new RpcProbeRequest(2))),
    ModNetworkTransport.Reliable, true, policyPeer));
Assert(NetworkRpcCodec.TryDecode(policyChannel.ClientPayload!, out var limitedPolicyMessage, out _) &&
       limitedPolicyMessage.Kind == NetworkRpcMessageKind.Error &&
       NetworkRpcCodec.DecodeError(limitedPolicyMessage.Body).Contains(
           "rate limit", StringComparison.OrdinalIgnoreCase) &&
       policyHandlerCalls == 0,
    "RPC rate limiting did not reject a peer after its burst was exhausted.");
policyRpc.Receive(new ModNetworkMessageEvent(
    "rpc", "ofs.framework.rpc:policy",
    NetworkRpcCodec.Encode(
        NetworkRpcMessageKind.Request, 103, policySerializer.Serialize(new RpcProbeRequest(2))),
    ModNetworkTransport.Reliable, true, new TestNetworkPeer()));
Assert(NetworkRpcCodec.TryDecode(policyChannel.ClientPayload!, out var allowedPolicyMessage, out _) &&
       allowedPolicyMessage.Kind == NetworkRpcMessageKind.Success &&
       policyHandlerCalls == 1,
    "RPC rate limiting leaked state across distinct peers or blocked an authorized request.");
policyRpc.Dispose();

var rpcChannel = new TestReplicatedChannel();
var rpcHandlerReady = false;
var rpc = new NetworkRpc<RpcProbeRequest, RpcProbeResponse>(
    "test.required",
    new ModRuntime.ModLogger("test.rpc"),
    new ModNetworkRpcDefinition<RpcProbeRequest, RpcProbeResponse>(
        "double",
        request => request.Value.Value >= 0
            ? new RpcProbeResponse(request.Value.Value * 2)
            : throw new InvalidOperationException("negative values are rejected"),
        MaxRequestBytes: 512,
        MaxResponseBytes: 512,
        DefaultTimeout: TimeSpan.FromMilliseconds(100)),
    rpcChannel,
    () => true,
    () => rpcHandlerReady,
    () => true,
    _ => { });
AssertThrows<InvalidOperationException>(() => rpc.InvokeServer(
        new RpcProbeRequest(1), _ => { }),
    "Network RPC sent before the client handler was ready.");
rpcHandlerReady = true;
ModNetworkRpcResult<RpcProbeResponse>? rpcResult = null;
var rpcCall = rpc.InvokeServer(new RpcProbeRequest(21), result => rpcResult = result);
Assert(rpcCall.IsPending && rpc.PendingCount == 1 && rpcChannel.ServerPayload is not null,
    "Network RPC did not track and send its pending request.");
rpc.Receive(new ModNetworkMessageEvent(
    "rpc",
    "ofs.framework.rpc:test",
    rpcChannel.ServerPayload!,
    ModNetworkTransport.Reliable,
    true,
    new TestNetworkPeer()));
Assert(rpcChannel.ClientPayload is not null &&
       NetworkRpcCodec.TryDecode(rpcChannel.ClientPayload, out var rpcSuccessMessage, out _) &&
       rpcSuccessMessage.Kind == NetworkRpcMessageKind.Success &&
       rpcSuccessMessage.RequestId == rpcCall.RequestId,
    "Network RPC server did not return a correlated success response.");
rpc.Receive(new ModNetworkMessageEvent(
    "rpc",
    "ofs.framework.rpc:test",
    rpcChannel.ClientPayload!,
    ModNetworkTransport.Reliable,
    false,
    null));
Assert(!rpcCall.IsPending && rpc.PendingCount == 0 &&
       rpcResult?.Status == ModNetworkRpcStatus.Succeeded &&
       rpcResult.Value.Value == new RpcProbeResponse(42),
    "Network RPC client did not complete a successful response.");

rpcResult = null;
var rejectedRpcCall = rpc.InvokeServer(new RpcProbeRequest(-1), result => rpcResult = result);
rpc.Receive(new ModNetworkMessageEvent(
    "rpc", "ofs.framework.rpc:test", rpcChannel.ServerPayload!,
    ModNetworkTransport.Reliable, true, new TestNetworkPeer()));
Assert(NetworkRpcCodec.TryDecode(rpcChannel.ClientPayload!, out var rejectedMessage, out _) &&
       rejectedMessage.Kind == NetworkRpcMessageKind.Error &&
       rejectedMessage.RequestId == rejectedRpcCall.RequestId,
    "Network RPC server did not correlate its remote error.");
rpc.Receive(new ModNetworkMessageEvent(
    "rpc", "ofs.framework.rpc:test", rpcChannel.ClientPayload!,
    ModNetworkTransport.Reliable, false, null));
Assert(rpcResult?.Status == ModNetworkRpcStatus.RemoteError &&
       rpcResult.Value.Error?.Contains("negative", StringComparison.Ordinal) == true,
    "Network RPC handler exception was not returned as a remote error.");

rpcResult = null;
var timedOutCall = rpc.InvokeServer(
    new RpcProbeRequest(5),
    result => rpcResult = result,
    TimeSpan.FromMilliseconds(100));
var lateRequestPayload = rpcChannel.ServerPayload!.ToArray();
Thread.Sleep(120);
((INetworkRpcRuntime)rpc).OnFrame(new FrameEvent(4, 0.016f, 0.016f));
Assert(!timedOutCall.IsPending && rpcResult?.Status == ModNetworkRpcStatus.TimedOut,
    "Network RPC pending call did not time out on the frame lifecycle.");
rpc.Receive(new ModNetworkMessageEvent(
    "rpc",
    "ofs.framework.rpc:test",
    NetworkRpcCodec.Encode(
        NetworkRpcMessageKind.Success,
        timedOutCall.RequestId,
        ModNetworkSerializers.Json<RpcProbeResponse>().Serialize(new RpcProbeResponse(10))),
    ModNetworkTransport.Reliable,
    false,
    null));
Assert(rpcResult?.Status == ModNetworkRpcStatus.TimedOut && rpc.PendingCount == 0,
    "Network RPC accepted a response after timeout.");
Assert(lateRequestPayload.Length > 0, "Network RPC timeout probe did not send a request.");

rpcResult = null;
var cancelledCall = rpc.InvokeServer(new RpcProbeRequest(3), result => rpcResult = result);
cancelledCall.Cancel();
Assert(!cancelledCall.IsPending && rpcResult?.Status == ModNetworkRpcStatus.Cancelled,
    "Network RPC cancellation did not complete on the main thread.");
rpc.Dispose();

var networkReference = new NetworkObjectReference(0xA1B2C3D4);
var networkReferenceSerializer = ModNetworkSerializers.Json<NetworkObjectReference>();
Assert(networkReference.IsValid &&
       networkReferenceSerializer.Deserialize(
           networkReferenceSerializer.Serialize(networkReference)) == networkReference &&
       !default(NetworkObjectReference).IsValid,
    "Network object reference was not portable through the default RPC serializer.");
var networkResolution = new NetworkObjectResolution(
    networkReference,
    new UnityObject(0xA100),
    new UnityObject(0xA200),
    NetworkObjectSide.Server | NetworkObjectSide.Client,
    true);
Assert(networkResolution.Side.HasFlag(NetworkObjectSide.Server) &&
       networkResolution.Side.HasFlag(NetworkObjectSide.Client) &&
       networkResolution.IsOwnedByMod,
    "Network object resolution did not retain host-side registry and ownership flags.");

Assert((int)ItemKind.Antique == 4 &&
       (int)ItemFilter.Recyclable == 8 &&
       (int)BuildingCategory.Decorations == 2 &&
       (int)MiningVfxKind.DynamiteHazard == 9 &&
       (int)MiningSfxKind.Concrete == 6 &&
       (int)MysteryItemKind.Scrap == 2 &&
       (int)UpgradeKind.AdvancedRefiningLicense == 21,
    "Typed content enums no longer match the reversed game values.");
Assert((int)ModKey.A == 15 && (int)ModKey.F12 == 105 && (int)ModKey.MediaForward == 126,
    "Public key enum no longer matches Unity Input System values.");
var localizationTerm = new LocalizationTermDefinition(
    "items.test.name",
    new Dictionary<string, string> { ["en"] = "Test", ["es"] = "Prueba" });
Assert(localizationTerm.Translations.Count == 2,
    "Localization term contract did not preserve translations.");

var crashJournal = new ModLoadJournal
{
    SessionId = "session-1",
    ModId = "test.crashing",
    Version = "1.2.3",
    Phase = "mod-load",
    ManifestPath = @"C:\Game\OFS\mods\test.crashing\manifest.json",
    AssemblyPath = @"C:\Game\OFS\mods\test.crashing\Test.dll",
    StartedAtUtc = DateTimeOffset.Parse("2026-07-19T10:00:00Z"),
    ProcessId = 42,
};
Assert(ModSafetyDocuments.Validate(crashJournal).Count == 0,
    "Valid crash journal was rejected.");
var firstQuarantine = ModSafetyDocuments.Recover(
    crashJournal,
    null,
    DateTimeOffset.Parse("2026-07-19T10:01:00Z"));
var secondQuarantine = ModSafetyDocuments.Recover(
    crashJournal with { SessionId = "session-2" },
    firstQuarantine,
    DateTimeOffset.Parse("2026-07-19T10:02:00Z"));
Assert(secondQuarantine.Entries.Count == 1 &&
       secondQuarantine.Entries[0].Occurrences == 2 &&
       secondQuarantine.Entries[0].SessionId == "session-2",
    "Crash recovery did not replace/increment the quarantined mod deterministically.");
Assert(ModSafetyDocuments.Validate(secondQuarantine).Count == 0,
    "Recovered quarantine document is invalid.");
var runtimeCrashQuarantine = ModSafetyDocuments.Recover(
    crashJournal with { SessionId = "runtime-session", Phase = "callback:event:SceneLoaded" },
    null,
    DateTimeOffset.Parse("2026-07-19T10:03:00Z"));
Assert(runtimeCrashQuarantine.Entries.Single().Reason.Contains(
        "guarded runtime callback",
        StringComparison.Ordinal) &&
       runtimeCrashQuarantine.Entries.Single().Phase == "callback:event:SceneLoaded",
    "Runtime callback recovery did not preserve distinct attribution evidence.");

var safetyRoot = Path.Combine(
    Path.GetTempPath(),
    "ofs-sdk-safety-tests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(safetyRoot);
try
{
    ModSafetyStore.Initialize(safetyRoot);
    var runtimeManifest = Manifest("test.runtime-crash", "1.0.0");
    var runtimeManifestPath = Path.Combine(safetyRoot, "mods", "manifest.json");
    var runtimeAssemblyPath = Path.Combine(safetyRoot, "mods", "Test.dll");
    var interruptedAttempt = ModSafetyStore.BeginAttempt(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    ModSafetyStore.UpdateAttempt(interruptedAttempt, "mod-load");
    Assert(File.Exists(Path.Combine(safetyRoot, "OFS", "safety", "load-journal.json")),
        "Runtime did not durably write its load journal.");

    // Reinitialization simulates the next process observing an unclosed journal.
    ModSafetyStore.Initialize(safetyRoot);
    var recoveredEntry = ModSafetyStore.GetEntry(runtimeManifest.Id);
    Assert(ModSafetyStore.IsQuarantined(runtimeManifest.Id) &&
           recoveredEntry?.Phase == "mod-load" &&
           recoveredEntry.Occurrences == 1,
        "Runtime did not quarantine an interrupted mod load.");
    Assert(ModSafetyStore.Clear(runtimeManifest.Id) &&
           !ModSafetyStore.IsQuarantined(runtimeManifest.Id) &&
           ModSafetyStore.WasClearedThisSession(runtimeManifest.Id),
        "Runtime quarantine could not be cleared recoverably.");

    ModSafetyStore.RegisterRuntimeMod(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    using (ModSafetyStore.EnterRuntimeCallback(runtimeManifest.Id, "event:SceneLoaded"))
    {
        var activeCallbackJournal = File.ReadAllText(
            Path.Combine(safetyRoot, "OFS", "safety", "load-journal.json"));
        Assert(activeCallbackJournal.Contains("callback:event:SceneLoaded", StringComparison.Ordinal),
            "Runtime callback journal did not durably identify the active phase.");
    }
    ModSafetyStore.Initialize(safetyRoot);
    Assert(!ModSafetyStore.IsQuarantined(runtimeManifest.Id),
        "A completed runtime callback was falsely quarantined.");

    var nestedManifest = Manifest("test.runtime-nested", "1.0.0");
    ModSafetyStore.RegisterRuntimeMod(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    ModSafetyStore.RegisterRuntimeMod(
        nestedManifest,
        Path.Combine(safetyRoot, "nested", "manifest.json"),
        Path.Combine(safetyRoot, "nested", "Nested.dll"));

    ModRuntime.NotifyMainMenuReady();
    var runtimeInfoRoot = Path.Combine(safetyRoot, "runtime-info");
    Directory.CreateDirectory(Path.Combine(runtimeInfoRoot, "OFS"));
    var runtimeFingerprint = new string('A', 64);
    File.WriteAllText(
        Path.Combine(runtimeInfoRoot, "OFS", "install-manifest.json"),
        JsonSerializer.Serialize(new { gameFingerprint = runtimeFingerprint }));
    var metadataPath = Path.Combine(runtimeInfoRoot, "global-metadata.dat");
    File.WriteAllBytes(metadataPath, [0xaf, 0x1b, 0xb1, 0xfa, 39, 0, 0, 0]);
    Assert(ModRuntimeInfo.ReadGameFingerprint(runtimeInfoRoot) ==
           runtimeFingerprint.ToLowerInvariant() &&
           ModRuntimeInfo.ReadMetadataVersion(metadataPath) == 39,
        "Runtime environment readers did not validate fingerprint/metadata headers.");
    var runtimeMainThread = false;
    var runtimeInfo = new ModRuntimeInfo(
        new Version(0, 1, 0),
        "1.0.2",
        runtimeFingerprint.ToLowerInvariant(),
        "6000.3.13f1",
        39,
        "X64",
        8,
        true,
        () => runtimeMainThread);
    Assert(!runtimeInfo.IsMainThread && runtimeInfo.IsVerifiedGameBuild &&
           runtimeInfo.FrameworkVersion == new Version(0, 1, 0) &&
           runtimeInfo.GameVersion == "1.0.2" &&
           runtimeInfo.UnityVersion == "6000.3.13f1" &&
           runtimeInfo.Il2CppMetadataVersion == 39 &&
           runtimeInfo.PointerSize == 8,
        "Runtime environment snapshot lost immutable build/ABI facts.");
    runtimeMainThread = true;
    Assert(runtimeInfo.IsMainThread,
        "Runtime environment did not expose the live main-thread state.");
    File.WriteAllBytes(metadataPath, new byte[8]);
    AssertThrows<InvalidDataException>(
        () => ModRuntimeInfo.ReadMetadataVersion(metadataPath),
        "Runtime environment accepted invalid IL2CPP metadata magic.");

    var diagnosticGame = Path.Combine(safetyRoot, "runtime-diagnostics");
    var diagnosticMods = Path.Combine(diagnosticGame, "OFS", "mods");
    Directory.CreateDirectory(diagnosticMods);
    ModDiagnosticsRuntime.Begin(diagnosticGame, runtimeInfo, 6);
    var diagnosticStatuses = new[]
    {
        ModStartupStatus.Loaded,
        ModStartupStatus.Disabled,
        ModStartupStatus.Quarantined,
        ModStartupStatus.Rejected,
        ModStartupStatus.Blocked,
        ModStartupStatus.Failed,
    };
    for (var index = 0; index < diagnosticStatuses.Length; ++index)
    {
        var status = diagnosticStatuses[index];
        var manifest = status == ModStartupStatus.Rejected
            ? null
            : Manifest($"test.diagnostic-{index}", "1.0.0");
        ModDiagnosticsRuntime.Record(
            Path.Combine(diagnosticMods, $"mod-{index}", "manifest.json"),
            manifest,
            status,
            status.ToString().ToLowerInvariant(),
            $"Diagnostic {status}.",
            status == ModStartupStatus.Blocked ? ["test.provider"] : []);
    }
    ModDiagnosticsRuntime.Complete();
    var diagnosticJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    var diagnosticPath = Path.Combine(
        diagnosticGame,
        "OFS",
        "diagnostics",
        "last-session.json");
    var diagnosticReport = JsonSerializer.Deserialize<RuntimeDiagnosticReport>(
        File.ReadAllText(diagnosticPath),
        diagnosticJsonOptions);
    Assert(diagnosticReport is not null &&
           diagnosticReport.SchemaVersion == RuntimeDiagnosticReport.CurrentSchemaVersion &&
           diagnosticReport.State == RuntimeStartupState.Ready &&
           diagnosticReport.Mods.Count == 6 &&
           diagnosticReport.LoadedCount == 1 &&
           diagnosticReport.ProblemCount == 4 &&
           diagnosticReport.HasProblems &&
           diagnosticReport.Mods.Select(value => value.Status)
               .OrderBy(value => value)
               .SequenceEqual(diagnosticStatuses.OrderBy(value => value)),
        "Runtime diagnostics did not persist every startup disposition.");
    ModDiagnosticsRuntime.Begin(diagnosticGame, runtimeInfo, 1);
    ModDiagnosticsRuntime.Record(
        Path.Combine(diagnosticMods, "next", "manifest.json"),
        Manifest("test.diagnostic-next", "1.0.0"),
        ModStartupStatus.Loaded,
        "load",
        "Loaded successfully.");
    ModDiagnosticsRuntime.Complete();
    var previousDiagnosticPath = Path.Combine(
        diagnosticGame,
        "OFS",
        "diagnostics",
        "previous-session.json");
    var previousDiagnostic = JsonSerializer.Deserialize<RuntimeDiagnosticReport>(
        File.ReadAllText(previousDiagnosticPath),
        diagnosticJsonOptions);
    Assert(previousDiagnostic?.Mods.Count == 6 &&
           previousDiagnostic.State == RuntimeStartupState.Ready,
        "Runtime diagnostics did not rotate the previous committed session atomically.");
    File.WriteAllText(diagnosticPath, "{ invalid");
    ModDiagnosticsRuntime.Begin(diagnosticGame, runtimeInfo, 1);
    ModDiagnosticsRuntime.Record(
        Path.Combine(diagnosticMods, "recovered", "manifest.json"),
        Manifest("test.diagnostic-recovered", "1.0.0"),
        ModStartupStatus.Loaded,
        "load",
        "Loaded successfully.");
    ModDiagnosticsRuntime.Complete();
    var recoveredDiagnostic = JsonSerializer.Deserialize<RuntimeDiagnosticReport>(
        File.ReadAllText(diagnosticPath),
        diagnosticJsonOptions);
    Assert(recoveredDiagnostic?.State == RuntimeStartupState.Ready &&
           recoveredDiagnostic.Mods.Count == 1 &&
           JsonSerializer.Deserialize<RuntimeDiagnosticReport>(
               File.ReadAllText(previousDiagnosticPath),
               diagnosticJsonOptions)?.Mods.Count == 6,
        "A corrupt last diagnostic prevented a new report or replaced the valid previous session.");

    var registrySource = new List<LoadedModDescriptor>
    {
        new(
            new ModInfo(
                "test.registry-provider",
                "Registry Provider",
                new Version(1, 2, 3),
                "",
                "Tests",
                safetyRoot),
            new Version(0, 1, 0),
            "client",
            ["factory.api"],
            []),
    };
    var registry = new ModRegistry(() => registrySource);
    Assert(registry.IsLoaded("TEST.REGISTRY-PROVIDER") &&
           registry.Get("test.registry-provider")?.Mod.Version == new Version(1, 2, 3) &&
           registry.FindByCapability("FACTORY.API").Count == 1 &&
           registry.Loaded.Count == 1,
        "Loaded-mod registry did not provide case-insensitive discovery snapshots.");
    var firstRegistrySnapshot = registry.Loaded;
    registrySource.Clear();
    Assert(firstRegistrySnapshot.Count == 1 && registry.Loaded.Count == 0,
        "Loaded-mod registry returned a mutable live collection instead of a snapshot.");
    Assert(firstRegistrySnapshot is not LoadedModDescriptor[],
        "Loaded-mod registry exposed its snapshot through a mutable array.");
    AssertThrows<ArgumentException>(
        () => registry.Get("aa"),
        "Loaded-mod registry accepted an invalid mod id lookup.");

    ModMessageBus.Initialize();
    var providerManifest = Manifest("test.bus-provider", "1.0.0");
    var consumerManifest = Manifest("test.bus-consumer", "1.0.0");
    ModSafetyStore.RegisterRuntimeMod(
        providerManifest,
        Path.Combine(safetyRoot, "bus-provider", "manifest.json"),
        Path.Combine(safetyRoot, "bus-provider", "Provider.dll"));
    ModSafetyStore.RegisterRuntimeMod(
        consumerManifest,
        Path.Combine(safetyRoot, "bus-consumer", "manifest.json"),
        Path.Combine(safetyRoot, "bus-consumer", "Consumer.dll"));
    var providerBus = new ModMessageBus(
        providerManifest.Id,
        new ModRuntime.ModLogger(providerManifest.Id));
    var consumerBus = new ModMessageBus(
        consumerManifest.Id,
        new ModRuntime.ModLogger(consumerManifest.Id));
    ModMessage? replayed = null;
    ModMessage? acknowledged = null;
    using var acknowledgement = providerBus.Subscribe(
        "consumer.ack",
        message => acknowledged = message,
        new ModMessageSubscriptionOptions(SenderModId: consumerManifest.Id));
    providerBus.Publish(
        "provider.ready",
        new byte[] { 1, 2, 3 },
        new ModMessagePublishOptions(Retain: true));
    using var replay = consumerBus.Subscribe(
        "provider.ready",
        message => replayed = message,
        new ModMessageSubscriptionOptions(
            SenderModId: providerManifest.Id,
            ReplayRetained: true));
    Assert(replayed is not null && replayed.Retained &&
           replayed.SenderModId == providerManifest.Id &&
           replayed.Payload.Span.SequenceEqual(new byte[] { 1, 2, 3 }),
        "Local message bus did not replay retained provider state.");
    consumerBus.Publish(
        "consumer.ack",
        new byte[] { 9 },
        new ModMessagePublishOptions(TargetModId: providerManifest.Id));
    Assert(acknowledged is not null && acknowledged.TargetModId == providerManifest.Id &&
           acknowledged.Payload.Span.SequenceEqual(new byte[] { 9 }),
        "Local message bus did not deliver a targeted acknowledgement.");
    var isolatedDelivery = 0;
    using var throwingSubscription = consumerBus.Subscribe(
        "provider.isolation",
        _ => throw new InvalidOperationException("expected bus isolation probe"));
    using var healthySubscription = consumerBus.Subscribe(
        "provider.isolation",
        _ => isolatedDelivery++);
    providerBus.Publish("provider.isolation", ReadOnlyMemory<byte>.Empty);
    Assert(isolatedDelivery == 1,
        "One failing local message handler blocked another subscription.");
    var recursiveDeliveries = 0;
    using var recursiveSubscription = consumerBus.Subscribe(
        "consumer.recursive",
        _ =>
        {
            recursiveDeliveries++;
            consumerBus.Publish("consumer.recursive", ReadOnlyMemory<byte>.Empty);
        });
    consumerBus.Publish("consumer.recursive", ReadOnlyMemory<byte>.Empty);
    Assert(recursiveDeliveries == ModMessageBusLimits.MaximumDispatchDepth,
        "Local message recursion was not bounded at the documented dispatch depth.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => providerBus.Publish(
            "provider.too-large",
            new byte[ModMessageBusLimits.MaximumPayloadBytes + 1]),
        "Local message bus accepted an oversized payload.");
    Assert(providerBus.RemoveRetained("provider.ready") &&
           !providerBus.RemoveRetained("provider.ready"),
        "Retained local message removal was not deterministic.");
    consumerBus.RemoveAll();
    providerBus.RemoveAll();
    Assert(!replay.IsSubscribed && !acknowledgement.IsSubscribed,
        "Local message subscriptions survived owner cleanup.");

    using (ModSafetyStore.EnterRuntimeCallback(runtimeManifest.Id, "event:MainMenuReady"))
    {
        using (ModSafetyStore.EnterRuntimeCallback(nestedManifest.Id, "main-menu-button:run"))
        {
            var nestedJournal = File.ReadAllText(
                Path.Combine(safetyRoot, "OFS", "safety", "load-journal.json"));
            Assert(nestedJournal.Contains(nestedManifest.Id, StringComparison.Ordinal) &&
                   nestedJournal.Contains("callback:main-menu-button:run", StringComparison.Ordinal),
                "Nested callback did not replace the durable marker with the active owner.");
        }
        var restoredJournal = File.ReadAllText(
            Path.Combine(safetyRoot, "OFS", "safety", "load-journal.json"));
        Assert(restoredJournal.Contains(runtimeManifest.Id, StringComparison.Ordinal) &&
               restoredJournal.Contains("callback:event:MainMenuReady", StringComparison.Ordinal),
            "Nested callback completion did not restore the outer attribution marker.");
    }
    ModSafetyStore.Initialize(safetyRoot);
    Assert(!ModSafetyStore.IsQuarantined(runtimeManifest.Id) &&
           !ModSafetyStore.IsQuarantined(nestedManifest.Id),
        "Completed nested callbacks were falsely quarantined.");

    ModSafetyStore.RegisterRuntimeMod(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    var interruptedCallback = ModSafetyStore.EnterRuntimeCallback(
        runtimeManifest.Id,
        "gameplay-panel:test:pressed");
    Assert(File.Exists(Path.Combine(safetyRoot, "OFS", "safety", "load-journal.json")),
        "Runtime callback did not write a durable crash marker.");
    ModSafetyStore.Initialize(safetyRoot);
    var runtimeCallbackEntry = ModSafetyStore.GetEntry(runtimeManifest.Id);
    Assert(runtimeCallbackEntry?.Phase == "callback:gameplay-panel:test:pressed" &&
           runtimeCallbackEntry.Reason.Contains("guarded runtime callback", StringComparison.Ordinal),
        "Interrupted runtime callback was not quarantined with callback evidence.");
    interruptedCallback.Dispose();
    Assert(ModSafetyStore.Clear(runtimeManifest.Id),
        "Runtime callback quarantine could not be cleared.");

    var completedAttempt = ModSafetyStore.BeginAttempt(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    ModSafetyStore.UpdateAttempt(completedAttempt, "mod-load");
    ModSafetyStore.CompleteAttempt(completedAttempt);
    ModSafetyStore.Initialize(safetyRoot);
    Assert(!ModSafetyStore.IsQuarantined(runtimeManifest.Id),
        "A completed managed load was falsely quarantined.");

    ModSafetyStore.RegisterRuntimeMod(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    var hotCallback = ModSafetyStore.EnterHotRuntimeCallback(
        runtimeManifest.Id,
        "event:FrameUpdate");
    var hotPath = Path.Combine(
        safetyRoot,
        "OFS",
        "safety",
        HotCrashBreadcrumbCodec.FileName);
    Assert(HotCrashBreadcrumbCodec.TryReadActive(
            ReadSharedFile(hotPath),
            out var activeHotJournal,
            out var activeHotError) &&
           activeHotError.Length == 0 &&
           activeHotJournal?.Phase == "callback:hot:event:FrameUpdate",
        "Hot callback breadcrumb did not publish valid active evidence.");
    hotCallback.Dispose();
    Assert(!HotCrashBreadcrumbCodec.TryReadActive(
            ReadSharedFile(hotPath),
            out _,
            out var completedHotError) &&
           completedHotError.Length == 0,
        "Completed hot callback left active crash evidence.");

    var interruptedHotCallback = ModSafetyStore.EnterHotRuntimeCallback(
        runtimeManifest.Id,
        "event:FrameUpdate");
    ModSafetyStore.Initialize(safetyRoot);
    var hotEntry = ModSafetyStore.GetEntry(runtimeManifest.Id);
    Assert(hotEntry?.Phase == "callback:hot:event:FrameUpdate" &&
           hotEntry.Reason.Contains("guarded runtime callback", StringComparison.Ordinal),
        "Interrupted hot callback was not recovered into quarantine.");
    interruptedHotCallback.Dispose();
    Assert(ModSafetyStore.Clear(runtimeManifest.Id),
        "Hot callback quarantine could not be cleared.");

    ModSafetyStore.RegisterRuntimeMod(
        runtimeManifest,
        runtimeManifestPath,
        runtimeAssemblyPath);
    var durablePrecedence = ModSafetyStore.EnterRuntimeCallback(
        runtimeManifest.Id,
        "scheduler:precise");
    var ambiguousHot = ModSafetyStore.EnterHotRuntimeCallback(
        runtimeManifest.Id,
        "event:FrameUpdate");
    ModSafetyStore.Initialize(safetyRoot);
    var precedenceEntry = ModSafetyStore.GetEntry(runtimeManifest.Id);
    Assert(precedenceEntry?.Phase == "callback:scheduler:precise" &&
           precedenceEntry.Occurrences == 1,
        "Hot evidence overrode or duplicated a more precise durable callback journal.");
    ambiguousHot.Dispose();
    durablePrecedence.Dispose();
    Assert(ModSafetyStore.Clear(runtimeManifest.Id),
        "Precedence callback quarantine could not be cleared.");

    using (var directHot = new HotRuntimeCallbackBreadcrumb())
    {
        var directRoot = Path.Combine(safetyRoot, "direct-hot");
        Assert(directHot.Initialize(directRoot) is null,
            "New hot callback store reported interrupted evidence.");
        var outer = directHot.Enter(
            runtimeManifest.Id,
            runtimeManifest.Version,
            runtimeManifestPath,
            runtimeAssemblyPath,
            "outer");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var concurrent = Task.Run(() =>
        {
            using var inner = directHot.Enter(
                runtimeManifest.Id,
                runtimeManifest.Version,
                runtimeManifestPath,
                runtimeAssemblyPath,
                "concurrent");
            entered.Set();
            release.Wait();
        });
        entered.Wait();
        var directPath = Path.Combine(directRoot, HotCrashBreadcrumbCodec.FileName);
        Assert(!HotCrashBreadcrumbCodec.TryReadActive(
                ReadSharedFile(directPath),
                out _,
                out var concurrentError) &&
               concurrentError.Length == 0,
            "Concurrent hot callbacks on different threads produced a false accusation.");
        release.Set();
        concurrent.GetAwaiter().GetResult();
        Assert(HotCrashBreadcrumbCodec.TryReadActive(
                ReadSharedFile(directPath),
                out var restoredOuter,
                out _) &&
               restoredOuter?.Phase == "callback:hot:outer",
            "Hot callback store did not restore unambiguous outer evidence.");
        outer.Dispose();
    }
}
finally
{
    ModSafetyStore.ResetForTests();
    Directory.Delete(safetyRoot, recursive: true);
}

InputRuntime.InitializeForTests();
try
{
    var input = new ModInput("test.input", new ModRuntime.ModLogger("test.input"));
    var pressedCount = 0;
    ModInputEvent lastInput = default;
    using var hotkey = input.Register(new ModInputActionDefinition(
        "ctrl-f8",
        InputBinding.ForKey(ModKey.F8),
        value =>
        {
            ++pressedCount;
            lastInput = value;
        },
        Modifiers: InputModifiers.Control));
    var inputStates = new Dictionary<InputBinding, InputRuntime.InputButtonState>
    {
        [InputBinding.ForKey(ModKey.F8)] = new(Pressed: true, Held: true, Released: false),
        [InputBinding.ForKey(ModKey.LeftCtrl)] = new(Pressed: false, Held: true, Released: false),
    };
    InputRuntime.PollForTests(new FrameEvent(1, 0.016f, 0.016f), inputStates);
    Assert(pressedCount == 1 &&
           lastInput.Trigger == InputTrigger.Pressed &&
           lastInput.Modifiers == InputModifiers.Control,
        "Modified input action did not dispatch its pressed edge.");
    InputRuntime.PollForTests(
        new FrameEvent(2, 0.016f, 0.016f),
        inputStates,
        selectedUi: true);
    Assert(pressedCount == 1, "Default input capture policy leaked through selected UI.");
    hotkey.CapturePolicy = InputCapturePolicy.Always;
    InputRuntime.PollForTests(
        new FrameEvent(3, 0.016f, 0.016f),
        inputStates,
        selectedUi: true);
    Assert(pressedCount == 2, "Always input capture policy did not bypass UI selection.");

    using var failingInput = input.Register(new ModInputActionDefinition(
        "failing",
        InputBinding.ForKey(ModKey.F9),
        _ => throw new InvalidOperationException("Expected test failure.")));
    InputRuntime.PollForTests(
        new FrameEvent(4, 0.016f, 0.016f),
        new Dictionary<InputBinding, InputRuntime.InputButtonState>
        {
            [InputBinding.ForKey(ModKey.F9)] = new(Pressed: true, Held: true, Released: false),
        });
    Assert(!failingInput.Enabled && failingInput.IsRegistered,
        "Failing input callback was not safely disabled.");
}
finally
{
    InputRuntime.ResetForTests();
}

var removedHookTargets = new List<nint>();
var hookSafetyRoot = Path.Combine(
    Path.GetTempPath(),
    "ofs-sdk-hook-safety-tests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(hookSafetyRoot);
ModSafetyStore.Initialize(hookSafetyRoot);
var hookManifest = Manifest("test.hooks", "1.0.0");
ModSafetyStore.RegisterRuntimeMod(
    hookManifest,
    Path.Combine(hookSafetyRoot, "mods", "manifest.json"),
    Path.Combine(hookSafetyRoot, "mods", "Hook.dll"));
var hookHotPath = Path.Combine(
    hookSafetyRoot,
    "OFS",
    "safety",
    HotCrashBreadcrumbCodec.FileName);
TestNativeDelegate originalHookDelegate = value => value + 7;
var originalHookPointer = Marshal.GetFunctionPointerForDelegate(originalHookDelegate);
HookRuntime.ConfigureForTests(
    (target, replacement) =>
    {
        Assert(target == HookUnsafeApi.Target && replacement != 0,
            "Declarative hook backend received an invalid target or replacement.");
        return (true, originalHookPointer);
    },
    target =>
    {
        removedHookTargets.Add(target);
        return true;
    });
try
{
    var hookApi = new HookUnsafeApi();
    var hooks = new ModHooks("test.hooks", new ModRuntime.ModLogger("test.hooks"), hookApi);
    var guardedHookObserved = false;
    TestNativeDelegate replacementHookDelegate = value =>
    {
        guardedHookObserved = HotCrashBreadcrumbCodec.TryReadActive(
            ReadSharedFile(hookHotPath),
            out var journal,
            out var error) &&
            error.Length == 0 &&
            journal?.Phase == "callback:hot:detour:typed-probe";
        return value * 2;
    };
    var definition = new Il2CppMethodDetourDefinition(
        "typed-probe",
        "Assembly-CSharp.dll",
        string.Empty,
        "MainMenuManager",
        "get_Instance",
        0);
    var hook = hooks.InstallIl2Cpp(definition, replacementHookDelegate);
    Assert(hook.IsInstalled &&
           hook.Target == HookUnsafeApi.Target &&
           hook.MethodInfo == HookUnsafeApi.MethodInfo &&
           hook.OriginalDelegate(5) == 12,
        "Declarative IL2CPP hook did not preserve its typed trampoline metadata.");
    var guardedReplacement = Marshal.GetDelegateForFunctionPointer<TestNativeDelegate>(
        hook.Replacement);
    Assert(guardedReplacement(6) == 12 && guardedHookObserved,
        "Declarative IL2CPP hook replacement was not guarded by a hot breadcrumb.");
    Assert(!HotCrashBreadcrumbCodec.TryReadActive(
            ReadSharedFile(hookHotPath),
            out _,
            out var completedHookError) &&
           completedHookError.Length == 0,
        "Completed declarative hook left active crash evidence.");

    var competingHooks = new ModHooks(
        "test.competing-hooks",
        new ModRuntime.ModLogger("test.competing-hooks"),
        hookApi);
    var collisionRejected = false;
    try
    {
        _ = competingHooks.InstallIl2Cpp(definition with { Id = "competing" }, replacementHookDelegate);
    }
    catch (InvalidOperationException exception)
    {
        collisionRejected = exception.Message.Contains("test.hooks", StringComparison.Ordinal);
    }
    Assert(collisionRejected, "A second mod was allowed to detour an owned IL2CPP target.");

    hooks.RemoveAll();
    Assert(!hook.IsInstalled && removedHookTargets.SequenceEqual([HookUnsafeApi.Target]),
        "Owner rollback did not remove the declarative IL2CPP hook exactly once.");
}
finally
{
    HookRuntime.ResetForTests();
    ModSafetyStore.ResetForTests();
    Directory.Delete(hookSafetyRoot, recursive: true);
    GC.KeepAlive(originalHookDelegate);
}

var declarativeNpc = new NpcDefinition(
    "worker",
    new UnityObject(0x5005),
    DisplayName: "Worker",
    RequireNavigation: true,
    DefaultMoveSpeed: 3f,
    Variants:
    [
        new NpcVisualVariantDefinition("red", new UnityObject(0x5006), "Red Worker"),
        new NpcVisualVariantDefinition("blue", new UnityObject(0x5007), "Blue Worker"),
    ],
    Behaviors:
    [
        new NpcBehaviorDefinition("idle", Update: (_, _) => { }),
    ]);
NpcApi.ValidateDefinition(declarativeNpc);
var duplicateVariantRejected = false;
try
{
    NpcApi.ValidateDefinition(declarativeNpc with
    {
        Variants =
        [
            new NpcVisualVariantDefinition("same", new UnityObject(1)),
            new NpcVisualVariantDefinition("SAME", new UnityObject(2)),
        ],
    });
}
catch (ArgumentException)
{
    duplicateVariantRejected = true;
}
Assert(duplicateVariantRejected, "Duplicate NPC visual variant ids were accepted.");

var vanillaEmployeeSnapshot = new VanillaEmployeeSnapshot(
    "employee-42",
    "Ada",
    "Lovelace",
    VanillaEmployeeType.Sorter,
    VanillaEmployeeWorkState.GoingToDropOff,
    IsCarrying: true,
    IsWorking: true);
Assert(vanillaEmployeeSnapshot.IsInitialized &&
       vanillaEmployeeSnapshot.DisplayName == "Ada Lovelace" &&
       (int)vanillaEmployeeSnapshot.WorkState == 4,
    "Vanilla employee snapshot did not preserve the observed T_Employee contract.");
Assert(!new VanillaEmployeeSnapshot(
        string.Empty,
        string.Empty,
        string.Empty,
        VanillaEmployeeType.Sorter,
        VanillaEmployeeWorkState.Idle,
        false,
        false).IsInitialized,
    "Uninitialized vanilla employee snapshot was reported as initialized.");
Assert(!typeof(INpcApi).GetMethod(nameof(INpcApi.FindVanillaEmployees))!.IsAbstract &&
       !typeof(INpcApi).GetMethod(nameof(INpcApi.TryGetVanillaEmployee))!.IsAbstract &&
       !typeof(INpcApi).GetMethod(nameof(INpcApi.HireVanillaEmployeeServer))!.IsAbstract &&
       !typeof(INpcApi).GetMethod(nameof(INpcApi.TryGetHiredVanillaEmployee))!.IsAbstract &&
       !typeof(INpcApi).GetMethod(nameof(INpcApi.FireVanillaEmployeeServer))!.IsAbstract &&
       !typeof(IUnityApi).GetMethod(nameof(IUnityApi.FindComponents))!.IsAbstract &&
       !typeof(IVanillaEmployeeController)
           .GetMethod(nameof(IVanillaEmployeeController.InitializeServer))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.GetFieldValue))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.SetFieldValue))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.GetClassMetadata))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.GetMethods))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.GetFields))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.SetStaticFieldValue))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.WriteStaticObjectReference))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.BoxValue))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.NewSingleArray))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.ReadSingleArray))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.NewArray))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.WriteArrayElementReference))!.IsAbstract &&
       !typeof(IUnsafeIl2CppApi).GetMethod(nameof(IUnsafeIl2CppApi.Invoke))!.IsAbstract &&
       !typeof(IMenuPanel).GetMethod(nameof(IMenuPanel.AddToggle))!.IsAbstract &&
       !typeof(IMenuPanel).GetMethod(nameof(IMenuPanel.AddChoice))!.IsAbstract &&
       !typeof(IMenuPanel).GetMethod(nameof(IMenuPanel.AddInput))!.IsAbstract &&
       !typeof(IMenuPanel).GetMethod(nameof(IMenuPanel.RemoveControl))!.IsAbstract &&
       !typeof(IMenuPanel).GetMethod(nameof(IMenuPanel.Clear))!.IsAbstract &&
       !typeof(INetworkApi).GetProperty(nameof(INetworkApi.LastRemediationPlan))!.GetMethod!.IsAbstract &&
       !typeof(IModContext).GetProperty(nameof(IModContext.GameplayUi))!.GetMethod!.IsAbstract,
    "New component/employee/menu/gameplay UI API members lost their backward-compatible default bodies.");
var referenceArgument = Il2CppArgument.FromReference((nint)0x1234);
var intArgument = Il2CppArgument.FromInt32(unchecked((int)0x78563412));
var boolArgument = Il2CppArgument.FromBoolean(true);
var vectorArgument = Il2CppArgument.FromValue(new UnityVector3(1f, 2f, 3f));
Assert(referenceArgument.Kind == Il2CppArgumentKind.Reference &&
       referenceArgument.Reference == (nint)0x1234 &&
       referenceArgument.ValueBytes.IsEmpty &&
       intArgument.Kind == Il2CppArgumentKind.Value &&
       intArgument.ValueBytes.Span.SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }) &&
       boolArgument.ValueBytes.Span.SequenceEqual(new byte[] { 1 }) &&
       vectorArgument.ValueBytes.Length == 12,
    "IL2CPP invocation arguments did not preserve ABI value/reference representation.");
AssertThrows<ArgumentException>(
    () => Il2CppArgument.FromValueBytes(ReadOnlySpan<byte>.Empty),
    "IL2CPP invocation accepted an empty value argument.");
var reflectedMethod = new Il2CppMethodMetadata(
    (nint)0x42,
    "SetValue",
    "System.Void",
    6,
    0,
    [new Il2CppParameterMetadata(0, "value", "System.Int32")]);
Assert(reflectedMethod.Parameters.Count == 1 &&
       reflectedMethod.Parameters[0].TypeName == "System.Int32",
    "IL2CPP metadata contracts did not preserve method parameters.");
IUnsafeIl2CppApi legacyUnsafeApi = new HookUnsafeApi();
AssertThrows<NotSupportedException>(
    () => legacyUnsafeApi.Invoke(HookUnsafeApi.MethodInfo, 0),
    "Legacy unsafe API implementation did not fail closed for marshalled invocation.");
var menuToggleDefinition = new MenuPanelToggleDefinition(
    "enabled",
    "ENABLED",
    true);
var menuChoiceDefinition = new MenuPanelChoiceDefinition(
    "difficulty",
    "DIFFICULTY",
    ["NORMAL", "HARD"],
    InitialIndex: 1);
var menuInputDefinition = new MenuPanelInputDefinition(
    "name",
    "NAME",
    InitialValue: "Crusher",
    Placeholder: "machine name",
    MaxLength: 48);
Assert(menuToggleDefinition.InitialValue &&
       menuToggleDefinition.OnText == "ON" &&
       menuChoiceDefinition.Options.SequenceEqual(["NORMAL", "HARD"]) &&
       menuChoiceDefinition.InitialIndex == 1 &&
       menuInputDefinition.InitialValue == "Crusher" &&
       menuInputDefinition.MaxLength == 48,
    "Interactive menu contracts did not preserve values or defaults.");
IMenuPanel legacyMenuPanel = new LegacyMenuPanel();
AssertThrows<NotSupportedException>(
    legacyMenuPanel.Clear,
    "A runtime without dynamic menu reconstruction did not fail closed.");
AssertThrows<NotSupportedException>(
    () => legacyMenuPanel.RemoveControl("row"),
    "A runtime without dynamic menu removal did not fail closed.");
var gameplayHudDefinition = new GameplayHudDefinition(
    "factory-status",
    "FACTORY STATUS",
    "ONLINE",
    GameplayUiAnchor.BottomLeft,
    OffsetX: 24f,
    OffsetY: 32f,
    Visible: false);
var gameplayPanelClosed = false;
var gameplayPanelDefinition = new GameplayPanelDefinition(
    "confirm-action",
    "CONFIRM",
    "Run the mod action?",
    _ => gameplayPanelClosed = true);
var gameplayButtonInvoked = false;
var gameplayButtonDefinition = new GameplayPanelButtonDefinition(
    "accept",
    "ACCEPT",
    () => gameplayButtonInvoked = true,
    ClosePanel: true);
Assert(gameplayHudDefinition.Anchor == GameplayUiAnchor.BottomLeft &&
       gameplayHudDefinition.OffsetX == 24f &&
       gameplayHudDefinition.OffsetY == 32f &&
       !gameplayHudDefinition.Visible &&
       gameplayPanelDefinition.Closed is not null &&
       gameplayButtonDefinition.ClosePanel,
    "Gameplay UI contracts did not preserve values or defaults.");
gameplayPanelDefinition.Closed!(new GameplayPanelClosedEvent(
    null!,
    GameplayPanelCloseReason.UserCancelled));
gameplayButtonDefinition.OnPressed();
Assert(gameplayPanelClosed && gameplayButtonInvoked,
    "Gameplay UI callbacks were not retained by their contract records.");
Assert(Enum.GetValues<GameplayUiAnchor>().Length == 4 &&
       Enum.GetValues<GameplayPanelCloseReason>().Length == 7,
    "Gameplay UI enum contracts changed unexpectedly.");
var vanillaInitialization = new VanillaEmployeeInitialization(
    "employee-42",
    "Ada",
    "Lovelace",
    new UnityObject(0x7010),
    new UnityObject(0x7020),
    Profile: VanillaEmployeeProfile.TechSmart,
    Intelligence: 5,
    DailyWage: 125,
    HiredDay: 3);
Assert(vanillaInitialization.Type == VanillaEmployeeType.Sorter &&
       vanillaInitialization.Profile == VanillaEmployeeProfile.TechSmart &&
       vanillaInitialization.Intelligence == 5 &&
       vanillaInitialization.HomePoint.Pointer == 0x7010,
    "Vanilla employee initialization contract did not preserve its values/defaults.");
var vanillaHire = new VanillaEmployeeHireDefinition(
    "sorter-01",
    "Grace",
    "Hopper",
    Profile: VanillaEmployeeProfile.StrongTough,
    Stamina: 5,
    DailyWage: 150);
var vanillaHired = new VanillaHiredEmployeeSnapshot(
    "test.npcs:sorter-01",
    vanillaHire.FirstName,
    vanillaHire.LastName,
    vanillaHire.Type,
    vanillaHire.Profile,
    vanillaHire.AvatarIndex,
    vanillaHire.Agility,
    vanillaHire.Intelligence,
    vanillaHire.Technique,
    vanillaHire.Stamina,
    vanillaHire.DailyWage,
    vanillaHire.HiredDay,
    IsFired: false,
    ActiveOffsiteContractId: string.Empty);
Assert(vanillaHired.QualifiedId == "test.npcs:sorter-01" &&
       vanillaHired.DisplayName == "Grace Hopper" &&
       !vanillaHired.IsOnOffsiteContract &&
       vanillaHire.Type == VanillaEmployeeType.Sorter &&
       !vanillaHire.BypassCapacity,
    "Persistent vanilla hire contracts did not preserve ownership or defaults.");

var npcEvents = new TestModEvents();
var npcRuntime = new NpcBehaviorRuntime(
    "test.npcs",
    npcEvents,
    new ModRuntime.ModLogger("test.npcs"));
var fakeNpc = new TestNpc("test.npcs", new UnityObject(0x6006));
var behaviorStarted = 0;
var behaviorUpdated = 0;
var behaviorStopped = 0;
var behavior = npcRuntime.Attach(fakeNpc, new NpcBehaviorDefinition(
    "patrol",
    _ => ++behaviorStarted,
    (_, _) => ++behaviorUpdated,
    _ => ++behaviorStopped));
Assert(behaviorStarted == 1 && behavior.IsAttached,
    "NPC behavior Started callback was not dispatched during attachment.");
npcEvents.RaiseFrame(new FrameEvent(10, 0.016f, 0.016f));
Assert(behaviorUpdated == 1, "NPC behavior did not receive its frame update.");

var duplicateBehaviorRejected = false;
try
{
    _ = npcRuntime.Attach(fakeNpc, new NpcBehaviorDefinition("PATROL", Update: (_, _) => { }));
}
catch (InvalidOperationException)
{
    duplicateBehaviorRejected = true;
}
Assert(duplicateBehaviorRejected, "Duplicate behavior id on one NPC was accepted.");

var behaviorOrder = new List<string>();
_ = npcRuntime.Attach(fakeNpc, new NpcBehaviorDefinition(
    "late",
    Update: (_, _) => behaviorOrder.Add("late"),
    Order: 10));
_ = npcRuntime.Attach(fakeNpc, new NpcBehaviorDefinition(
    "early",
    Update: (_, _) => behaviorOrder.Add("early"),
    Order: -10));
var failingBehavior = npcRuntime.Attach(fakeNpc, new NpcBehaviorDefinition(
    "failing",
    Update: (_, _) => throw new InvalidOperationException("Expected NPC behavior failure.")));
npcEvents.RaiseFrame(new FrameEvent(11, 0.016f, 0.016f));
Assert(behaviorOrder.SequenceEqual(["early", "late"]),
    "NPC behaviors did not run in deterministic order.");
Assert(!failingBehavior.Enabled && failingBehavior.IsAttached,
    "Failing NPC behavior was not isolated and disabled.");

fakeNpc.MarkDespawned();
npcEvents.RaiseFrame(new FrameEvent(12, 0.016f, 0.016f));
Assert(!behavior.IsAttached && behaviorStopped == 1,
    "NPC despawn did not detach behavior and dispatch Stopped exactly once.");
npcRuntime.RemoveAll();

var dialogueGraph = DialogueGraph.Validate(new DialogueDefinition(
    "intro",
    "hello",
    [
        new DialogueNodeDefinition(
            "hello",
            DialogueText.Plain("Foreman"),
            DialogueText.Term("Mods/test/dialogue/hello"),
            [new DialogueChoiceDefinition("continue", DialogueText.Plain("Continue"), "done")]),
        new DialogueNodeDefinition(
            "done",
            DialogueText.Plain("Foreman"),
            DialogueText.Plain("Done"),
            [new DialogueChoiceDefinition("close", DialogueText.Plain("Close"), Close: true)]),
    ]));
Assert(dialogueGraph.Nodes.Count == 2 && dialogueGraph.Nodes.ContainsKey("done"),
    "Dialogue graph validation did not preserve valid nodes.");
var invalidDialogueRejected = false;
try
{
    _ = DialogueGraph.Validate(new DialogueDefinition(
        "broken", "start",
        [new DialogueNodeDefinition(
            "start", DialogueText.Plain("NPC"), DialogueText.Plain("Broken"),
            [new DialogueChoiceDefinition("next", DialogueText.Plain("Next"), "missing")])]));
}
catch (ArgumentException) { invalidDialogueRejected = true; }
Assert(invalidDialogueRejected, "Dialogue graph accepted a missing target node.");

var interactionOrder = new List<string>();
var interactionDefinition = new InteractionDefinition(
    "probe",
    new UnityObject(0x7777),
    Primary: _ => interactionOrder.Add("callback"),
    PrimaryHandling: InteractionHandling.BeforeOriginal);
var interactionEvents = new TestModEvents();
var interactionLogger = new ModRuntime.ModLogger("test.interactions");
var interactionApi = new InteractionApi(
    "test.interactions", new HookUnsafeApi(), interactionEvents, interactionLogger);
var interactionRegistration = new InteractionRegistration(
    interactionApi,
    "test.interactions",
    interactionDefinition,
    new UnityObject(0x8888),
    new HookUnsafeApi(),
    interactionLogger);
InteractionRuntime.DispatchForTests(
    interactionRegistration,
    InteractionButton.Primary,
    () => interactionOrder.Add("original"));
Assert(interactionOrder.SequenceEqual(["callback", "original"]),
    "Interaction router did not honor BeforeOriginal ordering.");

EntityApi.ValidateDefinition(new EntityDefinition(
    "bundle-machine",
    new UnityObject(0x9191),
    Variants: [new EntityVisualVariantDefinition("red", new UnityObject(0x9292))],
    Behaviors: [new EntityBehaviorDefinition("tick", Update: (_, _) => { })],
    Interaction: new EntityInteractionDefinition(Primary: _ => { })));
var invalidEntityRejected = false;
try
{
    EntityApi.ValidateDefinition(new EntityDefinition(
        "broken-entity",
        new UnityObject(0x9191),
        Variants:
        [
            new EntityVisualVariantDefinition("same", new UnityObject(0x9292)),
            new EntityVisualVariantDefinition("same", new UnityObject(0x9393)),
        ]));
}
catch (ArgumentException) { invalidEntityRejected = true; }
Assert(invalidEntityRejected, "Entity definition accepted duplicate visual variants.");
var entityResolutionEvents = new TestModEvents();
var entityResolutionApi = new EntityApi(
    "test.entity-resolution",
    new TestWorldApi(),
    new TestUnityApi(),
    new TestInteractionApi(),
    entityResolutionEvents,
    new ModRuntime.ModLogger("test.entity-resolution"));
using var entityResolutionRegistration = entityResolutionApi.RegisterDefinition(new EntityDefinition(
    "machine",
    new UnityObject(0xA100),
    DisplayName: "Machine",
    Variants:
    [
        new EntityVisualVariantDefinition("same-prefab", new UnityObject(0xA100)),
        new EntityVisualVariantDefinition("alternate", new UnityObject(0xA200), "Alternate"),
    ]));
Assert(entityResolutionApi.GetDefinitionPrefabs("machine").Select(value => value.Pointer)
        .SequenceEqual([0xA100, 0xA200]),
    "Network entity prefab inventory did not deduplicate variants by pointer.");
var resolvedEntity = entityResolutionApi.Resolve(new EntitySpawnDefinition(
    "machine", UnityVector3.Zero, UnityQuaternion.Identity,
    VariantId: "alternate", Persistent: true, Active: false));
Assert(resolvedEntity.Prefab.Pointer == 0xA200 && resolvedEntity.Name == "Alternate" &&
       resolvedEntity.Persistent && !resolvedEntity.Active,
    "Network entity spawn resolution did not preserve variant and overrides.");
entityResolutionApi.RemoveAll();

var entityEvents = new TestModEvents();
var entityBehaviorOrder = new List<string>();
var entityRuntime = new EntityBehaviorRuntime(
    "test.entities", entityEvents, new ModRuntime.ModLogger("test.entities"));
var fakeEntity = new TestEntity("test.entities", new UnityObject(0x9494));
var lateEntityBehavior = entityRuntime.Attach(fakeEntity, new EntityBehaviorDefinition(
    "late", Update: (_, _) => entityBehaviorOrder.Add("late"), Order: 10));
var earlyEntityBehavior = entityRuntime.Attach(fakeEntity, new EntityBehaviorDefinition(
    "early", Update: (_, _) => entityBehaviorOrder.Add("early"), Order: -10));
var failingEntityBehavior = entityRuntime.Attach(fakeEntity, new EntityBehaviorDefinition(
    "failing", Update: (_, _) => throw new InvalidOperationException("expected"), Order: 0));
entityEvents.RaiseFrame(new FrameEvent(20, 0.016f, 0.016f));
Assert(entityBehaviorOrder.SequenceEqual(["early", "late"]),
    "Entity behaviors did not update in deterministic order.");
Assert(!failingEntityBehavior.Enabled && failingEntityBehavior.IsAttached,
    "Failing entity behavior was not isolated and disabled.");
fakeEntity.MarkDespawned();
entityEvents.RaiseFrame(new FrameEvent(21, 0.016f, 0.016f));
Assert(!earlyEntityBehavior.IsAttached && !lateEntityBehavior.IsAttached,
    "Entity despawn did not detach managed behaviors.");
entityRuntime.RemoveAll();

var bundleProbeDirectory = Path.Combine(
    Path.GetTempPath(), "ofs-sdk-bundle-index-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(bundleProbeDirectory);
try
{
    var baseBundlePath = Path.Combine(bundleProbeDirectory, "base.bundle");
    var entityBundlePath = Path.Combine(bundleProbeDirectory, "entities.bundle");
    File.WriteAllBytes(baseBundlePath, [1, 2, 3, 4]);
    File.WriteAllBytes(entityBundlePath, [5, 6, 7]);
    static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    var bundleIndexPath = Path.Combine(bundleProbeDirectory, "ofs-bundles.json");
    File.WriteAllText(bundleIndexPath, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        unityVersion = "6000.3.13f1",
        buildTarget = "StandaloneWindows64",
        bundles = new object[]
        {
            new
            {
                name = "entities.bundle",
                bytes = 3,
                sha256 = HashFile(entityBundlePath),
                unityHash = "entity-hash",
                dependencies = new[] { "base.bundle" },
            },
            new
            {
                name = "base.bundle",
                bytes = 4,
                sha256 = HashFile(baseBundlePath),
                unityHash = "base-hash",
                dependencies = Array.Empty<string>(),
            },
        },
    }));
    var bundleIndex = BundleIndexReader.Read(bundleIndexPath);
    var bundleOrder = BundleIndexReader.ResolveLoadOrder(bundleIndex);
    Assert(bundleOrder.Select(value => value.Name).SequenceEqual(["base.bundle", "entities.bundle"]),
        "AssetBundle dependency order was not topological and deterministic.");
    BundleIndexReader.VerifyFile(baseBundlePath, bundleOrder[0]);
    File.WriteAllBytes(baseBundlePath, [9, 9, 9, 9]);
    var bundleTamperRejected = false;
    try { BundleIndexReader.VerifyFile(baseBundlePath, bundleOrder[0]); }
    catch (InvalidDataException) { bundleTamperRejected = true; }
    Assert(bundleTamperRejected, "AssetBundle hash validation accepted tampered bytes.");

    var cyclicIndex = new BundleIndex
    {
        SchemaVersion = 1,
        UnityVersion = "6000.3.13f1",
        BuildTarget = "StandaloneWindows64",
        Bundles =
        [
            new BundleIndexRecord { Name = "a", Dependencies = ["b"] },
            new BundleIndexRecord { Name = "b", Dependencies = ["a"] },
        ],
    };
    var cycleRejected = false;
    try { _ = BundleIndexReader.ResolveLoadOrder(cyclicIndex); }
    catch (InvalidDataException) { cycleRejected = true; }
    Assert(cycleRejected, "AssetBundle dependency cycle was accepted.");
}
finally
{
    Directory.Delete(bundleProbeDirectory, recursive: true);
}

var saveMigrationRoot = Path.Combine(
    Path.GetTempPath(), "ofs-sdk-save-migrations-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(saveMigrationRoot);
try
{
    var freshSave = new ModSaveData("test.fresh-save", saveMigrationRoot);
    freshSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(
        CurrentVersion: 1,
        Steps: [],
        LegacyVersion: 1));
    freshSave.SetCurrentSlot(7);
    var freshResult = freshSave.ApplyRegisteredMigrations();
    Assert(freshResult.Status == SaveMigrationStatus.Initialized &&
           freshSave.SchemaVersion == 1 &&
           File.Exists(Path.Combine(freshSave.CurrentDirectory!, ".ofs-schema.json")),
        "Fresh save schema was not initialized without replaying legacy migrations.");
    AssertThrows<ArgumentException>(
        () => freshSave.Save("nested/../.ofs-schema.json", new { schemaVersion = 999 }),
        "Normalized user path was allowed to overwrite framework schema metadata.");

    var migratedSave = new ModSaveData("test.migrated-save", saveMigrationRoot);
    migratedSave.SetCurrentSlot(7);
    migratedSave.Save("state.json", new SaveProbe("legacy", 1));
    migratedSave.Save("obsolete.json", new SaveProbe("remove", 99));
    var migrationOrder = new List<string>();
    var stagingDirectories = new List<string>();
    migratedSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(
        CurrentVersion: 3,
        Steps:
        [
            new SaveMigrationStepDefinition(1, 2, migration =>
            {
                migrationOrder.Add("1->2");
                stagingDirectories.Add(migratedSave.CurrentDirectory!);
                var state = migration.Load("state.json", () => new SaveProbe("missing", 0));
                migration.Save("state.json", state with { Name = "upgraded", Count = state.Count + 1 });
            }),
            new SaveMigrationStepDefinition(2, 3, migration =>
            {
                migrationOrder.Add("2->3");
                stagingDirectories.Add(migratedSave.CurrentDirectory!);
                var state = migration.Load("state.json", () => new SaveProbe("missing", 0));
                migration.Save("state.json", state with { Count = state.Count * 10 });
                Assert(migration.Delete("obsolete.json"),
                    "Migration context could not delete an obsolete sidecar.");
            }),
        ],
        LegacyVersion: 1));
    var migratedResult = migratedSave.ApplyRegisteredMigrations();
    var migratedState = migratedSave.Load("state.json", () => new SaveProbe("missing", 0));
    Assert(migratedResult.Status == SaveMigrationStatus.Migrated &&
           migratedResult.FromVersion == 1 && migratedResult.ToVersion == 3 &&
           migratedSave.SchemaVersion == 3 &&
           migrationOrder.SequenceEqual(["1->2", "2->3"]) &&
           migratedState == new SaveProbe("upgraded", 20) &&
           !migratedSave.Exists("obsolete.json"),
        "Legacy sidecars did not migrate through the complete deterministic chain.");
    Assert(stagingDirectories.Count == 2 &&
           stagingDirectories.All(path => path.Contains(".migration-", StringComparison.Ordinal)) &&
           stagingDirectories.All(path => !Directory.Exists(path)),
        "Migration callbacks did not use an isolated staging directory or staging leaked.");
    Assert(migratedSave.ApplyRegisteredMigrations().Status == SaveMigrationStatus.UpToDate,
        "An up-to-date sidecar replayed its migration chain.");

    var failingSave = new ModSaveData("test.failed-save", saveMigrationRoot);
    failingSave.SetCurrentSlot(7);
    failingSave.Save("state.json", new SaveProbe("original", 5));
    var originalBytes = File.ReadAllBytes(Path.Combine(failingSave.CurrentDirectory!, "state.json"));
    failingSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(
        CurrentVersion: 2,
        Steps:
        [
            new SaveMigrationStepDefinition(1, 2, migration =>
            {
                migration.Save("state.json", new SaveProbe("corrupted", 500));
                throw new InvalidOperationException("expected rollback probe");
            }),
        ],
        LegacyVersion: 1));
    var failedResult = failingSave.ApplyRegisteredMigrations();
    Assert(failedResult.Status == SaveMigrationStatus.Failed &&
           failedResult.Error?.Contains("expected rollback probe", StringComparison.Ordinal) == true &&
           File.ReadAllBytes(Path.Combine(failingSave.CurrentDirectory!, "state.json"))
               .SequenceEqual(originalBytes) &&
           !File.Exists(Path.Combine(failingSave.CurrentDirectory!, ".ofs-schema.json")),
        "A failed migration changed live sidecar bytes or committed schema metadata.");

    var corruptSave = new ModSaveData("test.corrupt-schema", saveMigrationRoot);
    corruptSave.SetCurrentSlot(7);
    Directory.CreateDirectory(corruptSave.CurrentDirectory!);
    File.WriteAllText(Path.Combine(corruptSave.CurrentDirectory!, ".ofs-schema.json"), "{not-json");
    corruptSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(1, [], 1));
    Assert(corruptSave.ApplyRegisteredMigrations().Status == SaveMigrationStatus.Failed,
        "Corrupt schema metadata was accepted or overwritten.");

    var newerSave = new ModSaveData("test.newer-schema", saveMigrationRoot);
    newerSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(3, [], 3));
    newerSave.SetCurrentSlot(7);
    Assert(newerSave.ApplyRegisteredMigrations().Status == SaveMigrationStatus.Initialized,
        "Newer schema fixture was not initialized.");
    var downgradedRuntime = new ModSaveData("test.newer-schema", saveMigrationRoot);
    downgradedRuntime.RegisterMigrationPlan(new SaveMigrationPlanDefinition(2, [], 2));
    downgradedRuntime.SetCurrentSlot(7);
    var downgradeResult = downgradedRuntime.ApplyRegisteredMigrations();
    Assert(downgradeResult.Status == SaveMigrationStatus.Failed &&
           downgradeResult.Error?.Contains("downgrade", StringComparison.OrdinalIgnoreCase) == true &&
           downgradedRuntime.SchemaVersion == 3,
        "Runtime accepted a destructive save-schema downgrade.");

    var invalidPlanSave = new ModSaveData("test.invalid-plan", saveMigrationRoot);
    AssertThrows<ArgumentException>(
        () => invalidPlanSave.RegisterMigrationPlan(new SaveMigrationPlanDefinition(
            3,
            [new SaveMigrationStepDefinition(1, 2, _ => { })],
            1)),
        "Migration plan with a missing transition was accepted.");

    var modsDirectory = Path.Combine(saveMigrationRoot, "OFS_0007", "Mods");
    Assert(!Directory.EnumerateDirectories(modsDirectory, "*.migration-*").Any() &&
           !Directory.EnumerateDirectories(modsDirectory, "*.backup-*").Any(),
        "Migration staging or backup directories leaked after verification.");
}
finally
{
    Directory.Delete(saveMigrationRoot, recursive: true);
}

var resolvedProfileRoot = Path.Combine(
    Path.GetTempPath(),
    "ofs-sdk-remediation-profile",
    Guid.NewGuid().ToString("N"));
try
{
    foreach (var manifest in new[]
             {
                 Manifest("test.profile-a", "1.0.0"),
                 Manifest("test.profile-b", "1.0.0"),
             })
    {
        var directory = Path.Combine(resolvedProfileRoot, "OFS", "mods", manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }
    ModProfileStore.Initialize(resolvedProfileRoot);
    ModProfileStore.StageResolvedChanges(
        ["test.future-package"],
        ["test.profile-a"]);
    var pendingProfilePath = Path.Combine(
        resolvedProfileRoot,
        "OFS",
        "profiles",
        "pending.json");
    using (var pendingDocument = JsonDocument.Parse(File.ReadAllText(pendingProfilePath)))
    {
        var pendingIds = pendingDocument.RootElement.GetProperty("enabled")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert(pendingIds.SequenceEqual(["test.future-package", "test.profile-b"]),
            "Resolved remediation profile did not atomically stage enable/disable ids.");
    }
    var pendingBeforeConflict = File.ReadAllText(pendingProfilePath);
    AssertThrows<InvalidDataException>(
        () => ModProfileStore.StageResolvedChanges(
            ["test.profile-a"],
            ["TEST.PROFILE-A"]),
        "Resolved profile accepted the same id in enable and disable sets.");
    Assert(File.ReadAllText(pendingProfilePath) == pendingBeforeConflict,
        "Rejected remediation profile mutated the pending profile.");
    ModProfileStore.StageResolvedChanges(
        ["test.profile-a"],
        ["test.future-package"]);
    Assert(!File.Exists(pendingProfilePath),
        "Resolved profile equal to implicit active state left a restart marker.");
}
finally
{
    if (Directory.Exists(resolvedProfileRoot))
    {
        Directory.Delete(resolvedProfileRoot, recursive: true);
    }
}

Console.WriteLine("OFS.Sdk smoke tests passed.");
return;

static ModManifest Manifest(
    string id,
    string version,
    IReadOnlyList<ModDependency>? dependencies = null) => new()
    {
        Id = id,
        Name = id,
        Version = version,
        Assembly = "Test.dll",
        EntryPoint = "Test.EntryPoint",
        SdkVersion = "0.1.0",
        Dependencies = dependencies ?? [],
    };

static ModCatalogEntry CatalogEntry(
    string id,
    string name,
    string summary,
    string author,
    IReadOnlyList<string> capabilities) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        Summary = summary,
        Author = author,
        SdkVersion = "0.1.0",
        GameBuilds = ["*"],
        Capabilities = capabilities,
        Package = new ModCatalogPackage
        {
            Url = $"https://example.invalid/{id}.ofmod",
            Bytes = 1,
            Sha256 = new string('0', 64),
        },
    };

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TestNativeDelegate(int value);

internal sealed record ReplicatedProbe(string Status, int Count);
internal sealed record RpcProbeRequest(int Value);
internal sealed record RpcProbeResponse(int Doubled);
internal sealed record SaveProbe(string Name, int Count);

internal sealed class TestNetworkPeer : INetworkPeer
{
    public int ConnectionId => 7;
    public string Address => "test";
    public bool IsAuthenticated => true;
}

internal sealed class TestReplicatedChannel : IModNetworkChannel
{
    public string OwnerId => "ofs.framework.state";
    public string Id => "test";
    public string QualifiedId => "ofs.framework.state:test";
    public ModNetworkDirection Direction => ModNetworkDirection.Bidirectional;
    public int MaxPayloadBytes => NetworkEnvelopeCodec.MaxPayloadBytes;
    public bool IsRegistered { get; private set; } = true;
    public bool Enabled { get; set; } = true;
    public byte[]? ServerPayload { get; private set; }
    public byte[]? ClientPayload { get; private set; }
    public byte[]? BroadcastPayload { get; private set; }

    public void SendToServer(
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport = ModNetworkTransport.Reliable) =>
        ServerPayload = payload.ToArray();

    public void SendToClient(
        INetworkPeer peer,
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport = ModNetworkTransport.Reliable) =>
        ClientPayload = payload.ToArray();

    public void SendToAllClients(
        ReadOnlyMemory<byte> payload,
        ModNetworkTransport transport = ModNetworkTransport.Reliable,
        bool authenticatedOnly = true) =>
        BroadcastPayload = payload.ToArray();

    public void Unregister()
    {
        IsRegistered = false;
        Enabled = false;
    }

    public void Dispose() => Unregister();
}

internal sealed class ThumbnailHttpHandler(byte[] payload) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(payload),
        });
    }
}

internal sealed class HookUnsafeApi : IUnsafeIl2CppApi
{
    internal static readonly nint Class = 0x1001;
    internal static readonly nint MethodInfo = 0x2002;
    internal static readonly nint Target = 0x3003;

    public nint GameAssemblyModule => 1;
    public nint Domain => 2;

    public nint FindImage(string assemblyName) =>
        assemblyName == "Assembly-CSharp.dll" ? 3 : 0;

    public nint FindClass(string assemblyName, string namespaze, string name) =>
        assemblyName == "Assembly-CSharp.dll" &&
        namespaze.Length == 0 &&
        name == "MainMenuManager"
            ? Class
            : 0;

    public nint FindMethod(nint klass, string name, int argumentCount) =>
        klass == Class && name == "get_Instance" && argumentCount == 0
            ? MethodInfo
            : 0;

    public nint FindMethodBySignature(
        nint klass,
        string name,
        IReadOnlyList<string> parameterTypeNames) => 0;

    public nint ResolveVirtualMethod(nint instance, nint methodInfo) => throw Unsupported();

    public nint GetMethodPointer(nint methodInfo) => methodInfo == MethodInfo ? Target : 0;

    public nint FindNestedClass(nint declaringClass, string name) => throw Unsupported();
    public void EnsureClassInitialized(nint klass) => throw Unsupported();
    public nint NewObject(nint klass) => throw Unsupported();
    public nint ShallowCloneObject(nint instance) => throw Unsupported();
    public nint GetObjectClass(nint instance) => throw Unsupported();
    public bool IsAssignableFrom(nint baseClass, nint candidateClass) => throw Unsupported();
    public nint GetTypeObject(nint klass) => throw Unsupported();
    public nint FindField(nint klass, string name) => throw Unsupported();
    public nint GetFieldTypeClass(nint fieldInfo) => throw Unsupported();
    public int GetFieldOffset(nint fieldInfo) => throw Unsupported();
    public nint ReadObjectReference(nint instance, nint fieldInfo) => throw Unsupported();
    public nint ReadStaticObjectReference(nint fieldInfo) => throw Unsupported();
    public void WriteObjectReference(nint instance, nint fieldInfo, nint value) => throw Unsupported();
    public int ReadInt32(nint instance, nint fieldInfo) => throw Unsupported();
    public void WriteInt32(nint instance, nint fieldInfo, int value) => throw Unsupported();
    public float ReadSingle(nint instance, nint fieldInfo) => throw Unsupported();
    public void WriteSingle(nint instance, nint fieldInfo, float value) => throw Unsupported();
    public bool ReadBoolean(nint instance, nint fieldInfo) => throw Unsupported();
    public void WriteBoolean(nint instance, nint fieldInfo, bool value) => throw Unsupported();
    public nint Unbox(nint boxedValue) => throw Unsupported();
    public nint NewString(string value) => throw Unsupported();
    public string ReadString(nint value) => throw Unsupported();
    public nint NewByteArray(byte[] value) => throw Unsupported();
    public byte[] ReadByteArray(nint array) => throw Unsupported();
    public nuint GetArrayLength(nint array) => throw Unsupported();
    public nint ReadArrayElementReference(nint array, nuint index) => throw Unsupported();
    public nint RuntimeInvoke(nint methodInfo, nint instance, nint parameters) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("This member is outside the declarative hook smoke test.");
}

internal sealed class LegacyMenuPanel : IMenuPanel
{
    public string Id => "legacy";
    public string Title { get; set; } = "LEGACY";
    public bool Visible => false;
    public bool IsAlive => true;
    public IMenuButton AddButton(MenuPanelButtonDefinition definition) =>
        throw new NotSupportedException();
    public IMenuText AddText(MenuPanelTextDefinition definition) =>
        throw new NotSupportedException();
    public void Show() { }
    public void Close() { }
    public void Remove() { }
    public void Dispose() { }
}

internal sealed class TestModEvents : IModEvents
{
    public event Action<IMainMenuApi>? MainMenuReady { add { } remove { } }
    public event Action<SceneEvent>? SceneLoaded { add { } remove { } }
    public event Action<SceneEvent>? SceneUnloaded { add { } remove { } }
    public event Action? ContentReady { add { } remove { } }
    public event Action<SaveEvent>? SaveCompleted { add { } remove { } }
    public event Action<SaveEvent>? LoadCompleted { add { } remove { } }
    public event Action<FrameEvent>? FrameUpdate;

    public void RaiseFrame(FrameEvent frame) => FrameUpdate?.Invoke(frame);
}

internal sealed class TestNpc(string ownerId, UnityObject gameObject) : INpc
{
    public string OwnerId { get; } = ownerId;
    public UnityObject GameObject { get; } = gameObject;
    public UnityObject Animator => UnityObject.Null;
    public INpcNavigation? Navigation => null;
    public bool IsSpawned { get; private set; } = true;
    public UnityTransform Transform { get; set; } = UnityTransform.Identity;

    public void SetIdleAnimation(int index) { }
    public void PlayAction(int index) { }
    public void StopAction() { }
    public void Despawn() => IsSpawned = false;
    public void Dispose() => Despawn();
    public void MarkDespawned() => IsSpawned = false;
}

internal sealed class TestEntity(string ownerId, UnityObject gameObject) : IEntity
{
    public string OwnerId { get; } = ownerId;
    public string DefinitionId => "test";
    public string? VariantId => null;
    public UnityObject GameObject { get; } = gameObject;
    public bool Persistent => false;
    public bool IsSpawned { get; private set; } = true;
    public UnityTransform Transform { get; set; } = UnityTransform.Identity;
    public IInteractionRegistration? Interaction => null;
    public void Despawn() => IsSpawned = false;
    public void Dispose() => Despawn();
    public void MarkDespawned() => IsSpawned = false;
}

internal sealed class TestWorldApi : IWorldApi
{
    public ISpawnedObject Spawn(PrefabSpawnDefinition definition) =>
        throw new NotSupportedException("Spawn is outside the entity resolution test.");
}

internal sealed class TestInteractionApi : IInteractionApi
{
    public bool IsAvailable => false;
    public IInteractionRegistration Register(InteractionDefinition definition) =>
        throw new NotSupportedException("Interaction is outside the entity resolution test.");
}

internal sealed class TestUnityApi : IUnityApi
{
    private static NotSupportedException Unsupported() =>
        new("Unity calls are outside the entity resolution test.");
    public UnityObject CreateGameObject(string name, UnityObject parent = default) => throw Unsupported();
    public UnityObject FindActiveGameObject(string name) => throw Unsupported();
    public UnityObject FindChild(UnityObject parent, string name, bool recursive = true) => throw Unsupported();
    public UnityObject CloneGameObject(UnityObject original, UnityObject parent = default) => throw Unsupported();
    public UnityObject Instantiate(UnityObject prefab, UnityVector3 position, UnityQuaternion rotation, UnityObject parent = default) => throw Unsupported();
    public UnityObject GetComponent(UnityObject gameObject, string assemblyName, string namespaze, string className) => throw Unsupported();
    public UnityObject TryGetComponent(UnityObject gameObject, string assemblyName, string namespaze, string className) => throw Unsupported();
    public IReadOnlyList<UnityObject> FindComponents(string assemblyName, string namespaze, string className, bool activeOnly = true) => throw Unsupported();
    public UnityObject AddComponent(UnityObject gameObject, string assemblyName, string namespaze, string className) => throw Unsupported();
    public string GetName(UnityObject instance) => throw Unsupported();
    public void SetName(UnityObject instance, string name) => throw Unsupported();
    public void SetActive(UnityObject gameObject, bool active) => throw Unsupported();
    public void SetText(UnityObject gameObjectWithTextMeshPro, string text) => throw Unsupported();
    public UnityTransform GetTransform(UnityObject gameObject) => throw Unsupported();
    public void SetTransform(UnityObject gameObject, UnityTransform transform) => throw Unsupported();
    public void SetParent(UnityObject gameObject, UnityObject parent, bool worldPositionStays = true) => throw Unsupported();
    public void DontDestroyOnLoad(UnityObject instance) => throw Unsupported();
    public void Destroy(UnityObject instance) => throw Unsupported();
}
