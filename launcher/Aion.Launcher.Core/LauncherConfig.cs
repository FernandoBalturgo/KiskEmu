using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>
/// One server the launcher plays on. The launcher keeps one per saved server (from the server's /api/launcher/config,
/// <see cref="ServerSetup"/>). No server is built in: a launcher.json next to the exe (what a server's download ZIP
/// carries) adds that server at the first start; without one the player adds servers.
/// </summary>
public sealed record LauncherConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("accountUrl")] string AccountUrl,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string SavedDirSuffix,
    [property: JsonPropertyName("certificateThumbprint")] string? CertificateThumbprint = null,
    [property: JsonPropertyName("allowHttp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AllowHttp = false,
    [property: JsonPropertyName("clientVersions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientVersion[]? ClientVersions = null,
    [property: JsonPropertyName("selectedClientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedClientVersion = null,
    [property: JsonPropertyName("steamClientPath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SteamClientPath = null)
{
    /// <summary>
    /// A launcher.json. Without a savedDirSuffix the launcher derives one from the account address, the same one it
    /// derives when the player adds the server by hand (<see cref="ServerSetup.DerivedSuffix"/>).
    /// </summary>
    public static LauncherConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("launcher.json is empty");
        if (config.Name is null || config.AccountUrl is null || config.ServerAddress is null) throw new InvalidDataException("launcher.json needs name, accountUrl and serverAddress");
        if (string.IsNullOrEmpty(config.SavedDirSuffix) && Uri.TryCreate(config.AccountUrl, UriKind.Absolute, out var account))
            config = config with { SavedDirSuffix = ServerSetup.DerivedSuffix(account) };
        // AION_LAUNCHER_ALLOW_HTTP=1 lets a local server use plain http instead of https.
        if (config.AllowHttp || string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "true", StringComparison.OrdinalIgnoreCase))
            config = config with { AllowHttp = true };
        config.Validate();
        return config;
    }

    /// <summary>The server in a launcher.json next to the exe; null when there is none. An invalid file throws.</summary>
    public static LauncherConfig? LoadDefault(string path) => File.Exists(path) ? Load(path) : null;

    [JsonIgnore] public IPAddress Server => IPAddress.Parse(ServerAddress);

    public void Validate()
    {
        if (!IPAddress.TryParse(ServerAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("serverAddress must be an IPv4 address");
        if (!Uri.TryCreate(AccountUrl, UriKind.Absolute, out var account) || (!AllowHttp && account.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("accountUrl must be an https URL (set AION_LAUNCHER_ALLOW_HTTP=1 for http)");
        if (BootstrapPort is < 1 or > 65535) throw new InvalidDataException("bootstrapPort");
        if (SavedDirSuffix.Length is < 1 or > 32 || !SavedDirSuffix.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("savedDirSuffix must be 1-32 letters or digits");
        if (SavedDirSuffix.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("savedDirSuffix must not be the official game's (Steam)");
        if (CertificateThumbprint is { } pin && (pin.Length != 40 || !pin.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("certificateThumbprint must be 40 hex characters");
        foreach (var version in ClientVersions ?? [])
        {
            if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Name))
                throw new InvalidDataException("clientVersions entries need an id and a name");
            if (!version.Kind.Equals(ClientVersion.PrivateKind, StringComparison.OrdinalIgnoreCase) && !version.IsSteam)
                throw new InvalidDataException("clientVersions kind must be private or steam");
        }
    }

    /// <summary>The bootstrap URL for one launch: the launch authorization is the code in the path.</summary>
    public string BootstrapUrl(string authorization) => $"http://{ServerAddress}:{BootstrapPort}/c/{authorization}";
}
