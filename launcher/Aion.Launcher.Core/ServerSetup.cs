using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>What a server's /api/launcher/config says about it (server: Aion.Host LauncherEndpoints).</summary>
public sealed record ServerReply(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string? SavedDirSuffix = null,
    [property: JsonPropertyName("clientVersions")] ClientVersion[]? ClientVersions = null,
    [property: JsonPropertyName("selectedClientVersion")] string? SelectedClientVersion = null);

/// <summary>
/// A server found at an address the player typed (or a kisklauncher:// link filled in). <paramref name="UntrustedThumbprint"/>:
/// the server's certificate is not trusted by Windows (self-signed); the player compares this thumbprint with the one the
/// server's owner published before the launcher pins it.
/// </summary>
public sealed record ServerProbe(Uri AccountUrl, ServerReply Reply, string? UntrustedThumbprint);

public static class ServerSetup
{
    public const string ConfigPath = "/api/launcher/config";
    public const string LinkScheme = "kisklauncher";

    /// <summary>
    /// The account site's address from what the player typed: https only, no path. "host" and "host:port" get https://;
    /// an address without a port also tries the account site's usual port 8780 (<see cref="Candidates"/>).
    /// </summary>
    public static Uri Normalize(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (text.Length == 0) throw new InvalidDataException("Enter the server's address.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new InvalidDataException("That is not a server address. It looks like world.example.com or https://world.example.com:8780.");
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The server's address must start with https://.");
        if (uri.UserInfo.Length > 0) throw new InvalidDataException("The server's address must not contain a user name.");
        return new UriBuilder(Uri.UriSchemeHttps, uri.Host, uri.Port).Uri;
    }

    /// <summary>The addresses to try: as typed, then on port 8780 when no port was typed.</summary>
    public static IReadOnlyList<Uri> Candidates(string text)
    {
        var uri = Normalize(text);
        var typedPort = text.Contains("://", StringComparison.Ordinal) ? text.Split("://", 2)[1] : text;
        var hasPort = typedPort.Split('/')[0].Contains(':', StringComparison.Ordinal) && !typedPort.StartsWith('[');
        return hasPort || uri.Port == 8780 ? [uri] : [uri, new UriBuilder(Uri.UriSchemeHttps, uri.Host, 8780).Uri];
    }

    /// <summary>
    /// The address a kisklauncher://add?url=... link carries; null for any other link. The link only fills in the address:
    /// the player still sees the server and confirms it.
    /// </summary>
    public static string? AddressFromLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(LinkScheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.Host.Equals("add", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "url") return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    /// <summary>
    /// Asks the address for its launcher settings. A certificate Windows does not trust is still read (its thumbprint is
    /// returned for the player to confirm); nothing is sent but the request for the public settings.
    /// </summary>
    public static async Task<ServerProbe> ProbeAsync(string text, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in Candidates(text))
        {
            string? thumbprint = null;
            var untrusted = false;
            using var own = handler is null ? new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                {
                    thumbprint = certificate?.Thumbprint;
                    untrusted = errors != SslPolicyErrors.None;
                    return certificate is not null;
                },
            } : null;
            using var http = new HttpClient(handler ?? own!, disposeHandler: false) { BaseAddress = candidate, Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var response = await http.GetAsync(ConfigPath, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new AccountException("That server does not offer KiskLauncher's setup. Its owner needs to update KiskEmu.");
                if (!response.IsSuccessStatusCode) throw new AccountException($"The server answered {(int)response.StatusCode}.");
                var reply = await response.Content.ReadFromJsonAsync<ServerReply>(ct) ?? throw new AccountException("The server sent no settings.");
                Apply(null, candidate, reply, null);   // the same checks as when it is used
                return new ServerProbe(candidate, reply, untrusted ? thumbprint : null);
            }
            catch (HttpRequestException error) { last = new AccountException($"Could not reach {candidate.Authority} ({error.Message})."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = new AccountException($"{candidate.Authority} did not answer in time."); }
            catch (System.Text.Json.JsonException) { last = new AccountException($"{candidate.Authority} is not a KiskEmu server."); }
            catch (InvalidDataException error) { last = new AccountException($"The server's settings are not usable ({error.Message})."); }
        }
        throw last ?? new AccountException("Could not reach the server.");
    }

    /// <summary>
    /// The server's settings applied to what the launcher keeps for it: the address, port, versions and name follow the
    /// server; the saved-folder suffix and the certificate pin stay as they were first set (a moved saved folder would lose
    /// the player's game settings, a changed pin is never taken from the server). Validated.
    /// </summary>
    public static LauncherConfig Apply(LauncherConfig? kept, Uri accountUrl, ServerReply reply, string? pin)
    {
        var name = reply.Name?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 40)] : accountUrl.Host;
        var config = new LauncherConfig(name, accountUrl.GetLeftPart(UriPartial.Authority), reply.ServerAddress, reply.BootstrapPort,
            kept?.SavedDirSuffix ?? (reply.SavedDirSuffix is { Length: > 0 } suffix ? suffix : DerivedSuffix(accountUrl)),
            kept?.CertificateThumbprint ?? pin,
            kept?.AllowHttp ?? false,
            reply.ClientVersions is { Length: > 0 } versions ? versions : kept?.ClientVersions,
            reply.SelectedClientVersion ?? kept?.SelectedClientVersion,
            kept?.SteamClientPath);
        config.Validate();
        return config;
    }

    /// <summary>A saved-folder suffix of 32 letters and digits from the server's address, when the server sets none.</summary>
    public static string DerivedSuffix(Uri accountUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("kisklauncher:" + accountUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant()));
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(hash.Take(32).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    /// <summary>The thumbprint grouped for reading aloud and comparing: "AB:CD:...".</summary>
    public static string Readable(string thumbprint) =>
        string.Join(":", Enumerable.Range(0, thumbprint.Length / 2).Select(i => thumbprint.Substring(i * 2, 2))).ToUpperInvariant();
}
