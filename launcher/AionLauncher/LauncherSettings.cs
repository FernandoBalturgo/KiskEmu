using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// One saved server: its settings as the server last sent them (<see cref="ServerSetup.Apply"/>), and the player's
/// sign-in there. With "Keep me signed in" it keeps that server's session token (encrypted for this Windows user) so the
/// next start needs no sign-in until the server ends the session. Never a password.
/// </summary>
sealed class ServerEntry
{
    public LauncherConfig Config { get; set; } = null!;
    public string? Username { get; set; }
    /// <summary>The "Keep me signed in" choice (the box's default next time).</summary>
    public bool KeepSignedIn { get; set; } = true;
    /// <summary>Account session token, DPAPI-protected (current Windows user), base64; and when the server ends it.</summary>
    public string? SessionToken { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    /// <summary>The client version chosen on the Play page (an id from the server's versions).</summary>
    public string? SelectedClientVersion { get; set; }

    [JsonIgnore] public string Url => Config.AccountUrl;

    /// <summary>The kept account session, when there is one that has not ended yet.</summary>
    public string? Session()
    {
        if (SessionToken is null || SessionExpiresAt is { } ends && ends <= DateTimeOffset.UtcNow) return null;
        return LauncherSettings.Unprotect(SessionToken);
    }

    public void SetSession(string? token, DateTimeOffset? expiresAt)
    {
        SessionToken = token is null ? null : LauncherSettings.Protect(token);
        SessionExpiresAt = token is null ? null : expiresAt;
    }
}

/// <summary>A private copy of one client build (by its fingerprint): where it is and what was checked.</summary>
sealed class ClientCopy
{
    public string? Folder { get; set; }
    /// <summary>Sizes and write times of the quick-check files when they last passed; unchanged files skip re-hashing.</summary>
    public string? VerifiedStamp { get; set; }
    /// <summary>The server address the firewall rules let this copy reach (they are redone for another server).</summary>
    public string? FirewallAddress { get; set; }
}

/// <summary>
/// What the launcher remembers (%LOCALAPPDATA%\A2CorePrivate\launcher-settings.json): the saved servers, the private
/// game copies (one per client build, shared by every server on that build), and during a download the Steam sign-in
/// (encrypted, to resume; removed when the game is installed). Settings of launchers before saved servers are moved into
/// this layout once (<see cref="Migrate"/>).
/// </summary>
sealed class LauncherSettings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A2CorePrivate");
    static string FilePath => Path.Combine(Folder, "launcher-settings.json");
    /// <summary>Build references downloaded from servers, by fingerprint.</summary>
    public static string BuildsFolder => Path.Combine(Folder, "builds");

    public List<ServerEntry> Servers { get; set; } = [];
    /// <summary>The account URL of the server in use.</summary>
    public string? CurrentServer { get; set; }
    /// <summary>The server of the launcher.json next to the exe, once added, so a removed one does not come back.</summary>
    public string? SeededDefault { get; set; }
    public Dictionary<string, ClientCopy> Copies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Run without installing" was chosen: the exe is used where it is and no longer offers to install itself.</summary>
    public bool NoInstall { get; set; }
    /// <summary>Folder of a started (possibly interrupted) Steam download, and the build it is for.</summary>
    public string? DownloadFolder { get; set; }
    public string? DownloadBuild { get; set; }
    public string? SteamAccount { get; set; }
    /// <summary>Steam refresh token, DPAPI-protected (current Windows user), base64.</summary>
    public string? SteamToken { get; set; }
    /// <summary>When true, copy z_A2Core_Patch-Windows_P.* from the client editor deploy folder before starting the game.</summary>
    public bool AllowClientPatchPaks { get; set; }

    // Launchers before saved servers kept one server's sign-in and one game copy here (read once by Migrate).
    public string? InstallFolder { get; set; }
    public string? VerifiedStamp { get; set; }
    public string? Username { get; set; }
    public bool? KeepSignedIn { get; set; }
    public string? SessionToken { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public string? SelectedClientVersion { get; set; }

    [JsonIgnore] public ServerEntry? Current => Servers.FirstOrDefault(s => s.Url.Equals(CurrentServer, StringComparison.OrdinalIgnoreCase)) ?? Servers.FirstOrDefault();

    public ServerEntry? Find(string accountUrl) => Servers.FirstOrDefault(s => s.Url.Equals(accountUrl, StringComparison.OrdinalIgnoreCase));

    public ClientCopy Copy(string fingerprint) => Copies.TryGetValue(fingerprint, out var copy) ? copy : Copies[fingerprint] = new ClientCopy();

    /// <summary>
    /// The server of a launcher.json next to the exe becomes a saved server once (and the current one: the player opened
    /// that server's download). The older launcher's game copy becomes the copy of the embedded build (the one it was
    /// checked against); its user name goes to the first server added (<see cref="AdoptLegacySignIn"/>).
    /// </summary>
    public bool Migrate(LauncherConfig? defaultServer, string embeddedFingerprint)
    {
        var changed = false;
        if (defaultServer is not null && !defaultServer.AccountUrl.Equals(SeededDefault, StringComparison.OrdinalIgnoreCase))
        {
            if (Find(defaultServer.AccountUrl) is null)
            {
                var entry = new ServerEntry { Config = defaultServer };
                AdoptLegacySignIn(entry);
                Servers.Insert(0, entry);
            }
            SeededDefault = CurrentServer = defaultServer.AccountUrl;
            changed = true;
        }
        if (InstallFolder is not null)
        {
            var copy = Copy(embeddedFingerprint);
            copy.Folder ??= InstallFolder;
            copy.VerifiedStamp ??= VerifiedStamp;
            copy.FirewallAddress ??= defaultServer?.ServerAddress;
            changed = true;
        }
        if (DownloadFolder is not null && DownloadBuild is null) { DownloadBuild = embeddedFingerprint; changed = true; }
        if (InstallFolder is not null || VerifiedStamp is not null) changed = true;
        InstallFolder = VerifiedStamp = null;
        // The older launcher's kept session was for the server built into it, which this launcher cannot know: it is
        // dropped rather than sent to another server.
        if (SessionToken is not null || SessionExpiresAt is not null) changed = true;
        SessionToken = null;
        SessionExpiresAt = null;
        if (CurrentServer is null && Servers.Count > 0) { CurrentServer = Servers[0].Url; changed = true; }
        return changed;
    }

    /// <summary>
    /// The first server added after an older launcher (before saved servers) gets its user name, "Keep me signed in"
    /// choice and client version, so the sign-in page is filled in as before.
    /// </summary>
    public void AdoptLegacySignIn(ServerEntry entry)
    {
        if (Servers.Count > 0 || Username is null && KeepSignedIn is null && SelectedClientVersion is null) return;
        entry.Username ??= Username;
        entry.KeepSignedIn = KeepSignedIn ?? entry.KeepSignedIn;
        entry.SelectedClientVersion ??= SelectedClientVersion;
        Username = SelectedClientVersion = null;
        KeepSignedIn = null;
    }

    public SteamDownload.SignIn? SteamSignIn()
    {
        if (SteamAccount is null || SteamToken is null) return null;
        return Unprotect(SteamToken) is { } refreshToken ? new(SteamAccount, refreshToken) : null;
    }

    public void SetSteamSignIn(SteamDownload.SignIn? signIn)
    {
        SteamAccount = signIn?.Account;
        SteamToken = signIn is null ? null : Protect(signIn.RefreshToken);
    }

    public static string Protect(string secret) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string protectedBase64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static LauncherSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath), Json) ?? new() : new();
            var allowHttp = string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "true", StringComparison.OrdinalIgnoreCase);
            // A saved server whose settings no longer pass the checks (edited by hand) is dropped rather than used.
            settings.Servers.RemoveAll(s =>
            {
                var cfg = allowHttp ? s.Config with { AllowHttp = true } : s.Config;
                try { cfg.Validate(); return false; }
                catch (Exception e) when (e is InvalidDataException or NullReferenceException) { return true; }
            });
            settings.Copies = new Dictionary<string, ClientCopy>(settings.Copies, StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        try { if (Directory.Exists(BuildsFolder)) Directory.Delete(BuildsFolder, recursive: true); } catch (IOException) { }
    }
}
