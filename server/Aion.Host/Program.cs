using Aion.Application.Characters;
using Aion.Application.Chat;
using Aion.Host.Chat;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Authentication;
using Aion.Host.Native;
using Aion.Host.Platform;
using Aion.Host.NcGuard;
using Aion.Host;
using Aion.Host.Debug;
using Aion.Host.Logging;
using Aion.World;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using Aion.Core;
using Aion.Infrastructure.Postgres;
using Npgsql;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddServerFileLog(builder.Configuration);
// Auth host and world servers (as an authserver and its worldservers, sharing one database): with account logins the
// host that runs the login port is the auth host (login, world list, account site, platform bootstrap, chat, staff
// dashboard) and runs no world itself. Every world is a world server, a host in world role (Aion:Role=world) running one
// world's game port only: managed ones are started by the auth host (Aion.Host.Worlds.WorldSupervisor), remote ones run
// on other machines against the same database. A development host (no database) still runs login and game in one process.
var worldProcess = Aion.Host.Worlds.WorldProcessOptions.From(builder.Configuration);
if (worldProcess is not null) builder.Services.AddSingleton(worldProcess);
var debugOptions = builder.Configuration.GetSection(AionConfiguration.Debug).Get<DebugOptions>() ?? new DebugOptions();
if (debugOptions.Enabled)
{
    builder.Services.AddSingleton(_ => OpcodeCatalog.Load());
    builder.Services.AddSingleton(debugOptions);
    builder.Services.AddSingleton<PlaytestDebugSink>();
}
// Bind and advertised addresses (loopback unless configured for a public server).
var network = builder.Configuration.GetSection(AionConfiguration.Network).Get<NetworkOptions>() ?? new NetworkOptions();
network.Validate();
builder.Services.AddSingleton(network);
var progressionSection = builder.Configuration.GetSection(AionConfiguration.Progression).Get<ProgressionOptions>() ?? new ProgressionOptions();
var levelCap = progressionSection.LevelCap is > 0 and <= ProgressionOptions.TableMaxLevel
    ? progressionSection.LevelCap
    : ProgressionOptions.DefaultLevelCap;
builder.Services.AddSingleton(new ProgressionOptions { LevelCap = levelCap });
builder.Services.AddSingleton(_ => Aion.World.GameData.Embedded.WithLevelCap(levelCap));
builder.Services.AddSingleton<IItemStatRoller>(sp => new ItemStatRollerService(sp.GetRequiredService<GameData>(), Random.Shared));
if (builder.Configuration["Aion:Debug:ShopFile"] is { Length: > 0 } shopDebug)
    Aion.World.GameSession.ConfigureShopDebug(shopDebug);
var shopPremium = Aion.World.ShopOptions.Parse(builder.Configuration["Aion:Shop:PremiumMode"]);
Aion.World.GameSession.ConfigureShopOptions(new Aion.World.ShopOptions
{
    PremiumMode = shopPremium,
    PassUnlockPrice = Math.Clamp(builder.Configuration.GetValue("Aion:Shop:PassUnlockPrice", Aion.World.GameSession.PaidPassUnlockQunaCost), 0, 1_000_000),
});
Aion.World.GameSession.ConfigureMembership(builder.Configuration.GetValue("Aion:World:MembershipByDefault", true));
// Databases (server/DATABASES.md). The auth host (and every non-world host) uses the realm database: accounts, staff
// tools, the world registry and the character index (ConnectionStrings:Aion for the game role, :Auth for accounts). A
// world server uses its own world database only (ConnectionStrings:World) and reaches the realm over the realm link.
var connectionString = builder.Configuration.GetConnectionString("Aion");
var authConnectionString = builder.Configuration.GetConnectionString("Auth");
var realmLink = Aion.Host.Realm.RealmLinkOptions.From(builder.Configuration, worldProcess);
if (worldProcess is not null)
{
    connectionString = builder.Configuration.GetConnectionString("World");
    authConnectionString = null;
    if (string.IsNullOrWhiteSpace(connectionString) || realmLink is null)
        throw new InvalidOperationException("World role needs ConnectionStrings:World (its own database) and Aion:Realm:Url and Aion:Realm:Token (the realm link).");
}
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<PostgresCharacterRepository>();
    builder.Services.AddSingleton<ICharacterRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<ICharacterStateRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<IWireCharacterRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<CharacterService>();
    builder.Services.AddSingleton<CharacterPersistenceService>();
    // Quna is the account's wallet (server/DATABASES.md): a staff grant of the Quna item to a character is refused, the
    // account page adds Quna in the realm.
    builder.Services.AddSingleton<IGameplayStore>(sp => new PostgresGameplayStore(
        sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ProgressionOptions>(), sp.GetService<IItemStatRoller>())
    { QunaWallet = sp.GetService<IQunaWallet>() });
    if (worldProcess is null)
    {
        builder.Services.AddSingleton<IGameTicketRedeemer, PostgresGameTicketRedeemer>();
        builder.Services.AddSingleton<IWorldRegistry, PostgresWorldRegistry>();
        builder.Services.AddSingleton<IRealmAccounts, PostgresRealmAccounts>();
    }
    else builder.Services.AddSingleton<IAccountMirror, PostgresAccountMirror>();
}
var accountsReady = !string.IsNullOrWhiteSpace(connectionString) && (!string.IsNullOrWhiteSpace(authConnectionString) || realmLink is not null);
if (accountsReady && worldProcess is null)
{
    builder.Services.AddSingleton<PostgresAccountAuthentication>(provider =>
    {
        var auth = new PostgresAccountAuthentication(authConnectionString!) { TotpIssuer = SiteName(builder.Configuration) };
        auth.TotpEnabled = builder.Configuration.GetValue("Aion:Totp:Enabled", true);
        var totpKey = builder.Configuration["Aion:Totp:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(totpKey) && builder.Configuration["Aion:BridgeSecret"] is { Length: >= 32 } bridge)
            totpKey = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bridge)));
        if (auth.TotpEnabled && !string.IsNullOrWhiteSpace(totpKey)) auth.ConfigureTotp(totpKey);
        return auth;
    });
    builder.Services.AddSingleton<IAccountAuthentication>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IClientHandoff>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IAccountManagement>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<ITotpAuthentication>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    // Staff dashboard sessions: password, staff role and authenticator code (migration 098).
    builder.Services.AddSingleton<IStaffSessions>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    // Accounts for the account page and the staff Accounts page (migration 099).
    builder.Services.AddSingleton<IAccountDirectory>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IPlatformSecondaryAuth>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IWorldSessions>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddHostedService<AuthenticationCleanup>();
    // Staff tools: maintenance, sanctions, chat log, tickets, events, population, economy (migration 028).
    builder.Services.AddSingleton<IStaffTools, PostgresStaffTools>();
    // Custom shop goods (migration 094): edited on the dashboard, listed in the shop catalogue, read by the worlds.
    builder.Services.AddSingleton<PostgresCustomShop>();
    builder.Services.AddSingleton<ICustomShopAdmin>(provider => provider.GetRequiredService<PostgresCustomShop>());
    builder.Services.AddSingleton<ICustomShopCatalog>(provider => provider.GetRequiredService<PostgresCustomShop>());
    // The account Quna wallets (migration 095): the realm's; changes are pushed to the world the account plays on.
    builder.Services.AddSingleton<PostgresQunaWallet>();
    builder.Services.AddSingleton<IQunaWallet>(provider =>
        provider.GetService<Aion.Host.Worlds.WorldPresenceMirror>() is { } mirror && provider.GetService<Aion.Host.Worlds.WorldLinks>() is { } links
            ? new Aion.Host.Worlds.PushingQunaWallet(provider.GetRequiredService<PostgresQunaWallet>(), mirror, links,
                provider.GetRequiredService<ILogger<Aion.Host.Worlds.PushingQunaWallet>>())
            : provider.GetRequiredService<PostgresQunaWallet>());
    builder.Services.AddSingleton<Aion.Host.Authentication.ServerControl>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Authentication.ServerControl>());
}
else if (accountsReady)
{
    // World server: game logins, staff sign-ins, bans, maintenance and reports go through the realm link; it keeps its
    // account mirror and the realm's character index current.
    var linkWorldId = builder.Configuration.GetValue<int?>("Aion:Native:WorldId") ?? Aion.World.DevelopmentDefaults.WorldId;
    // The realm socket (server/REALM_LINK.md): realm-link calls ride it while it is open; Aion:Realm:Socket=false keeps HTTP only.
    var realmSocket = builder.Configuration.GetValue<bool?>("Aion:Realm:Socket") ?? true;
    builder.Services.AddSingleton<Aion.Host.Realm.LinkSlot>();
    builder.Services.AddSingleton(provider => new Aion.Host.Realm.RealmLinkClient(realmLink!, linkWorldId,
        provider.GetRequiredService<ILogger<Aion.Host.Realm.RealmLinkClient>>(), realmSocket ? provider.GetRequiredService<Aion.Host.Realm.LinkSlot>() : null));
    if (realmSocket)
        builder.Services.AddHostedService(provider => new Aion.Host.Realm.RealmSocketClient(realmLink!, linkWorldId,
            Aion.Host.Realm.RealmSocketClient.ControlAddress(worldProcess!.ListenAddress, builder.Configuration.GetValue<int?>("Aion:LocalPort") ?? 8780),
            provider.GetRequiredService<Aion.Host.Realm.LinkSlot>(), provider, provider.GetRequiredService<ILogger<Aion.Host.Realm.RealmSocketClient>>()));
    builder.Services.AddSingleton<Aion.Host.Realm.AccountMirrorService>();
    builder.Services.AddSingleton<IAccountAuthentication, Aion.Host.Realm.LinkedAccountAuthentication>();
    builder.Services.AddSingleton<IWorldSessions, Aion.Host.Realm.LinkedWorldSessions>();
    builder.Services.AddSingleton<IStaffTools>(provider => new Aion.Host.Realm.LinkedStaffTools(
        new PostgresStaffTools(provider.GetRequiredService<NpgsqlDataSource>()), provider.GetRequiredService<Aion.Host.Realm.RealmLinkClient>()));
    builder.Services.AddSingleton<Aion.Host.Authentication.ServerControl>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Authentication.ServerControl>());
    builder.Services.AddSingleton<Aion.Host.Realm.RealmLinkService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Realm.RealmLinkService>());
    builder.Services.AddSingleton<ICustomShopCatalog, Aion.Host.Realm.LinkedCustomShopCatalog>();
    // Quna is the realm's account wallet (server/DATABASES.md): spent and refunded over the realm link; only the realm adds Quna.
    builder.Services.AddSingleton<IQunaWallet, Aion.Host.Realm.LinkedQunaWallet>();
    builder.Services.AddSingleton<IShopLedger>(provider => new PostgresShopLedger(provider.GetRequiredService<NpgsqlDataSource>()));
}
// Optional Lime chat API for the dev client (policy ChatServerURL), on its own loopback port.
var chatPort = worldProcess is null ? builder.Configuration.GetValue<int?>("Aion:ChatPort") : null;
builder.Services.AddSingleton(TimeProvider.System);
// --- Chat identity (server/CHAT.md) ---
// Development logins accept the character the client names (explicit switch only). Otherwise
// a chat login needs the account's game ticket and binds to the account's character in the
// world on this host (native game listener with account logins); without those, every chat
// login is rejected.
var chatServerKey = builder.Configuration["Aion:ChatServerKey"] ?? Aion.World.DevelopmentDefaults.WorldIdString;
var chatWorldId = int.TryParse(chatServerKey, out var parsedChatWorld) ? parsedChatWorld : Aion.World.DevelopmentDefaults.WorldId;
// On the auth host one chat server serves every world server: its directory is the characters the world servers report.
static IChatDirectory? ChatDirectory(IServiceProvider provider, int world) =>
    provider.GetService<Aion.Host.Worlds.WorldPresenceMirror>() is { } mirror && provider.GetService<IRealmAccounts>() is { } index
        ? new Aion.Host.Worlds.RealmChatDirectory(null, mirror, index)
    : provider.GetService<IWireCharacterRepository>() is { } characters && provider.GetService<Aion.World.WorldPresence>() is { } presence
        ? new WorldChatDirectory(presence, characters, world) : null;
builder.Services.AddSingleton(provider => new ChatRelay(provider.GetRequiredService<TimeProvider>(), ChatDirectory(provider, chatWorldId)));
// The server's name (Aion:Web:SiteName) on both web sites and in authenticator apps; trimmed, at most 40 characters.
static string SiteName(IConfiguration configuration) =>
    configuration["Aion:Web:SiteName"]?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 40)] : "KiskEmu";
builder.Services.AddSingleton<StompHub>();
if (builder.Configuration.GetValue<bool>("Aion:ChatDevelopmentLogins"))
    builder.Services.AddSingleton<IChatLoginVerifier, DevelopmentChatLoginVerifier>();
else
    builder.Services.AddSingleton<IChatLoginVerifier>(provider =>
        provider.GetService<IClientHandoff>() is { } handoff && ChatDirectory(provider, chatWorldId) is { } directory
            ? provider.GetService<Aion.Host.Worlds.WorldLinks>() is not null
                // Auth host: the server key is the world the client logged in to (its ticket was routed there).
                ? new AccountChatLoginVerifier(handoff, directory, key =>
                    int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var world)
                    && world is >= 1 and <= 65535 ? world : null,
                    ticketWorldId: builder.Configuration.GetValue<int?>("Aion:Native:WorldId") ?? Aion.World.DevelopmentDefaults.WorldId)
                : new AccountChatLoginVerifier(handoff, directory, chatServerKey, chatWorldId)
            : new RejectingChatLoginVerifier());
// --- end chat identity ---
// Native client ports (port of tools/login_server.py and game_server.py) on the network bind address.
// "accounts" logins check launch tickets and store characters; "development" accepts any
// local client with an unsaved character and must be chosen explicitly.
var worldOptions = builder.Configuration.GetSection(AionConfiguration.World).Get<WorldOptions>() ?? new WorldOptions();
builder.Services.AddSingleton(worldOptions);
var askNpcFallback = worldOptions.ResolveMapEventAskNpcProximityFallback(debugOptions);
var native = (builder.Configuration.GetSection(AionConfiguration.Native).Get<NativeOptions>() ?? new NativeOptions()) with
{
    Network = network,
    MapEventAskNpcProximityFallback = askNpcFallback,
};
if (worldProcess is not null)
{
    if (native.GamePort is null || native.Development || !accountsReady)
        throw new InvalidOperationException("World role needs Aion:Native:GamePort, account logins (not development) and the realm link.");
    native = native with { LoginPort = null };
}
// The auth host runs no world of its own: world servers are separate processes. GamePort is only nulled here when it
// wasn't explicitly configured, so a single-box dev host can serve both login and game on one process.
var authHost = worldProcess is null && native.LoginPort is not null && !native.Development && accountsReady;
if (authHost && native.GamePort is null) native = native with { GamePort = null };
if (native.LoginPort is not null || native.GamePort is not null)
{
    if (!native.Development && !accountsReady)
        throw new InvalidOperationException("Native ports with account logins need the Aion and Auth connection strings; set Aion:Native:Logins=development for local testing.");
    builder.Services.AddSingleton(native);
    // NPCs walk on the extracted landscape where a ground grid exists (extracted data, not in the repository).
    var terrainStore = new Aion.World.TerrainStore(Aion.World.TerrainStore.Locate(native.TerrainDirectory, AppContext.BaseDirectory));
    builder.Services.AddSingleton(terrainStore);
    // NPC paths around obstacles where a navigation mesh exists (extracted data, not in the repository).
    var navStore = new Aion.World.NavMeshStore(Aion.World.NavMeshStore.Locate(native.NavMeshDirectory, AppContext.BaseDirectory));
    builder.Services.AddSingleton(navStore);
    foreach (var map in native.PreloadMaps)
    {
        // Background warm-up; a failed load surfaces when a session uses the map.
        var geometry = Aion.World.GameData.Embedded.GeometryMap(map);
        _ = terrainStore.LoadAsync(geometry);
        _ = navStore.LoadAsync(geometry);
    }
    // Builder (GM) commands: client console "s <command>" (0x362e) and "//<command>" in chat.
    builder.Services.AddSingleton<Aion.World.WorldPresence>();
    builder.Services.AddSingleton<ShopQunaBalanceService>();
    builder.Services.AddSingleton<Aion.Host.Authentication.ScheduledRestart>();
    // Multiplayer: one shared world (maps, NPCs, players) for every game connection on this host.
    builder.Services.AddSingleton(provider => new Aion.World.SharedWorld(provider.GetRequiredService<Aion.World.GameData>()));
    // The race of the world each account picks on the login port, for the game port's lobby.
    builder.Services.AddSingleton<Aion.World.WorldChoices>();
    builder.Services.AddSingleton(provider =>
    {
        var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Aion.Gm");
        return new Aion.World.BuilderCommands(provider.GetRequiredService<Aion.World.WorldPresence>(), provider.GetRequiredService<Aion.World.GmPolicy>(),
            (actor, command, result) => log.LogInformation("GM {Command} by {Actor}: {Result}", command, actor, result),
            provider.GetRequiredService<Aion.World.GameData>());
    });
    builder.Services.AddSingleton<IChatCommandHandler>(provider => authHost
        // Auth host: a chat command is answered by the world server the sender plays on.
        ? new Aion.Host.Worlds.RealmChatCommands(null, provider.GetRequiredService<Aion.Host.Worlds.WorldLinks>(), "")
        : new Aion.World.GmCommands(provider.GetRequiredService<Aion.World.WorldPresence>(), provider.GetRequiredService<Aion.World.BuilderCommands>())
        {
            // "//report <text>" opens a ticket for the staff dashboard (accounts only).
            Report = provider.GetService<IStaffTools>() is { } staffTools ? async (sender, name, text, ct) =>
            {
                if (sender.AccountId is not { } account) return "Reports need a signed-in account.";
                try { return $"Thanks, report #{await staffTools.CreateTicketAsync(account, name, "other", text, ct)} was sent to the staff."; }
                catch (GameplayException error) { return error.Code == "too_many_tickets" ? "You already have 10 open reports. Please wait for the staff." : "That report could not be sent."; }
            } : null,
        });
    // Staff rights for builder commands and the Login_RS admin flag (one check, from account roles).
    builder.Services.AddSingleton(provider =>
    {
        var store = provider.GetService<IGameplayStore>();
        return new Aion.World.GmPolicy
        {
            DevelopmentNames = native.Development ? native.DevelopmentGmNames.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(),
            StaffRole = async (account, ct) =>
            {
                if (store is null) return null;
                try { return Aion.Core.Characters.CharacterNames.DisplayRole((await store.AdminProfileAsync(new Aion.Core.Accounts.VerifiedAccount(account), ct)).Roles); }
                catch (GameplayException) { return null; }  // "forbidden": not staff
            },
            SearchItems = store is null ? null : async (account, query, ct) =>
                (await store.AdminSearchItemsAsync(new Aion.Core.Accounts.VerifiedAccount(account), query, ct)).Select(r => (r.TemplateId, r.Name)).ToList(),
            // Same audited, idempotent store path as the staff dashboard (applies on the next world entry).
            GrantItem = store is null ? null : async (account, character, item, count, ct) =>
            {
                var staff = new Aion.Core.Accounts.VerifiedAccount(account);
                var build = (await store.AdminProfileAsync(staff, ct)).BuildId ?? throw new GameplayException("no_catalogue");
                await store.AdminAsync(staff, new AdminCommand(Guid.NewGuid(), character, "grant", BuildId: build, TemplateId: item, Quantity: count), ct);
            },
            SetLevel = store is null ? null : (account, character, level, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "level", Level: level), ct),
            SetAscension = store is null ? null : (account, character, grade, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "ascension", Ascension: grade), ct),
            AcquireWings = store is null ? null : (account, character, wings, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "wings", Wings: wings), ct),
            ChangeClass = store is null ? null : async (account, character, name, ct) =>
            {
                var staff = new Aion.Core.Accounts.VerifiedAccount(account);
                var classes = (await store.AdminCharacterAsync(staff, character, ct))?.Classes ?? throw new GameplayException("character_unavailable");
                var available = classes.Select(c => c.Name).ToList();
                var match = classes.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is null) return (false, null, available);
                await store.AdminAsync(staff, new AdminCommand(Guid.NewGuid(), character, "class", CreationId: match.CreationId), ct);
                return (true, match.Name, available);
            },
        };
    });
    if (native.LoginPort is not null) builder.Services.AddHostedService<NativeLoginListener>();
    // Auth host: the world registry's list for the login port, the managed world servers, the links to every world server
    // (chat, staff tools) and the characters in each world for the realm's chat.
    if (authHost)
    {
        // Each world server's realm socket (server/REALM_LINK.md): both directions of the links below, HTTP as the fallback.
        builder.Services.AddSingleton<Aion.Host.Realm.RealmSocketHub>();
        builder.Services.AddSingleton<Aion.Host.Worlds.WorldSupervisor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Worlds.WorldSupervisor>());
        builder.Services.AddSingleton(provider => new Aion.Host.Worlds.LoginWorldList(provider.GetRequiredService<IWorldRegistry>(), native,
            provider.GetService<Aion.Host.Worlds.WorldSupervisor>(), provider.GetRequiredService<ILogger<Aion.Host.Worlds.LoginWorldList>>()));
        builder.Services.AddSingleton<Aion.Host.Worlds.WorldLinks>();
        builder.Services.AddSingleton<Aion.Host.Worlds.WorldPresenceMirror>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Worlds.WorldPresenceMirror>());
    }
    if (native.GamePort is not null) builder.Services.AddHostedService<NativeGameListener>();
    if (native.GamePort is not null)
    {
        builder.Services.AddSingleton<WorldLoadReporter>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<WorldLoadReporter>());
    }
    // Dashboard grants reach players in the world at once (needs the game database).
    if (native.GamePort is not null && !string.IsNullOrWhiteSpace(connectionString))
    {
        builder.Services.AddHostedService<InventoryNotifications>();
        // Database-wide sweep: the primary host runs it, not every world process.
        if (worldProcess is null) builder.Services.AddHostedService<PostExpirySweep>();
    }
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Chat WebSockets are long-lived; they would hold a host permit for their whole lifetime.
    // Chat messages are limited per character by ChatRelay instead.
    // Page bundles and wardrobe files (hundreds of icons at once over HTTP/2) are plain file reads.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        chatPort is { } chat && context.Connection.LocalPort == chat
            ? RateLimitPartition.GetNoLimiter("chat")
            : context.Request.Path.StartsWithSegments("/wardrobe") || context.Request.Path.StartsWithSegments("/assets")
            ? RateLimitPartition.GetNoLimiter("files")
            // A world server's realm socket lives as long as the world (its tunnelled calls come back through here).
            : context.Request.Path.StartsWithSegments(Aion.Host.Realm.RealmSocketHub.Path)
            ? RateLimitPartition.GetNoLimiter("realm-socket")
            : RateLimitPartition.GetConcurrencyLimiter("host", _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 }));
    options.AddPolicy("account-entry", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Staff dashboard reads and commands; role checks happen per request in the store.
    options.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("totp-verify", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
// Optional platform bootstrap for the dev client (GamePlatform.ini ConfigurationServerUrl).
var platform = worldProcess is null ? builder.Configuration.GetSection(AionConfiguration.Platform).Get<PlatformOptions>() ?? new PlatformOptions() : new PlatformOptions();
var platformResponses = platform.Port is not { } bootstrapPort ? null : PlatformResponses.Load(platform, network, bootstrapPort, native.LoginPort, chatPort);
// Launchers that sign in on this host and start clients without a launch code in the bootstrap URL.
if (platform.Port is not null && accountsReady) builder.Services.AddSingleton<LaunchPairing>();
var ncGuard = (worldProcess is null ? builder.Configuration.GetSection(AionConfiguration.NcGuard).Get<NcGuardOptions>() ?? new NcGuardOptions() : new NcGuardOptions())
    with { BindAddress = network.BindAddress };
if (ncGuard.Port is not null)
{
    builder.Services.AddSingleton(ncGuard);
    builder.Services.AddSingleton<NcGuardSink>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<NcGuardSink>());
    builder.Services.AddOptions<KestrelServerOptions>().Configure<NcGuardSink>((options, sink) => sink.Configure(options));
}
// Account site and API (sign-in, launches, staff dashboard). A public host serves it over HTTPS
// (Aion:Https PEM certificate) unless a TLS proxy in front of it is declared (Aion:Https:Proxy).
var port = builder.Configuration.GetValue<int?>("Aion:LocalPort") ?? 8780;
// The staff dashboard on a port of its own (Aion:Web:StaffPort; WebSites.cs), same TLS and bind rules as the account
// port. Unset: the dashboard stays on the account port at Aion:Web:StaffPath. Never on a world server.
var staffPort = worldProcess is null ? builder.Configuration.GetValue<int?>("Aion:Web:StaffPort") : null;
if (staffPort is { } chosenStaffPort && (chosenStaffPort == port || chosenStaffPort is < 1 or > 65535))
    throw new InvalidOperationException("Aion:Web:StaffPort must be a free port other than the account port (Aion:LocalPort).");
var https = builder.Configuration.GetSection(AionConfiguration.Https).Get<HttpsOptions>() ?? new HttpsOptions();
// AION_LAUNCHER_ALLOW_HTTP=1 serves the account port over plain HTTP (no TLS) for a local server.
var allowHttp = string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("AION_LAUNCHER_ALLOW_HTTP"), "true", StringComparison.OrdinalIgnoreCase);
if (allowHttp) https = new HttpsOptions { Proxy = false };
// A world server's HTTP port (status and control for the auth host, staff tools of this world) is plain HTTP on
// Aion:WorldProcess:ListenAddress (loopback unless a remote world server is reached over the network).
if (worldProcess is not null) https = new HttpsOptions { Proxy = true };
if (!allowHttp && !network.LoopbackOnly && https.CertificateFile is null && !https.Proxy)
    throw new InvalidOperationException("A public host needs Aion:Https:CertificateFile and KeyFile for the account port (or Aion:Https:Proxy=true behind a TLS proxy).");
var accountCertificate = allowHttp ? null : (https.CertificateFile is { } certificateFile
    ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certificateFile, https.KeyFile) : null);
// Windows TLS (Schannel) cannot use the ephemeral key of a PEM-loaded certificate; reload it as PKCS#12.
if (accountCertificate is not null && OperatingSystem.IsWindows())
    accountCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        accountCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16384;
    // Behind a proxy the account port stays on loopback; the proxy is the public endpoint.
    options.Listen(worldProcess is not null ? IPAddress.Parse(worldProcess.ListenAddress) : https.Proxy ? IPAddress.Loopback : network.Bind, port, listener =>
    {
        if (accountCertificate is not null) listener.UseHttps(accountCertificate);
    });
    if (staffPort is { } dashboardPort)
        options.Listen(https.Proxy ? IPAddress.Loopback : network.Bind, dashboardPort, listener =>
        {
            if (accountCertificate is not null) listener.UseHttps(accountCertificate);
        });
    // The client's own bootstrap and chat requests are plain HTTP (observed policy URLs).
    foreach (var httpPort in new[] { chatPort, platform.Port }.OfType<int>().Where(p => p != port).Distinct())
        options.Listen(network.Bind, httpPort);
});
builder.Services.AddSingleton<WardrobeAccess>();
var app = builder.Build();
if (worldProcess is not null)
{
    app.Logger.LogInformation("World server for world {World} ({Name}) on game port {Port}", native.WorldId, worldProcess.Name, native.GamePort);
    Aion.World.GameSession.ConfigureCustomShop(app.Services.GetService<ICustomShopCatalog>());
    Aion.Host.Worlds.WorldEndpoints.MapWorldProcessEndpoints(app, worldProcess, native.WorldId);
    // The staff dashboard's world tools (maintenance, announcements, players, live map, restart) for this world, reached
    // through the auth host (?world=…); every request still needs a staff sign-in.
    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed." });
    }));
    app.MapGameplayEndpoints(accounts: false);
    Aion.Host.Authentication.StaffEndpoints.MapStaffEndpoints(app);
    Aion.Host.Authentication.StaffToolsEndpoints.MapStaffToolsEndpoints(app);
    app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
    app.Run();
    return;
}
if (debugOptions.Enabled && app.Services.GetService<PlaytestDebugSink>() is { } playtestDebug)
{
    PlaytestDebug.Sink = playtestDebug;
    app.Lifetime.ApplicationStopping.Register(() => playtestDebug.DisposeAsync().AsTask().GetAwaiter().GetResult());
}
if (app.Services.GetService<ServerFileLoggerProvider>() is { } serverLog)
{
    app.Logger.LogInformation("Server log file: {Path}", serverLog.Path);
    app.Lifetime.ApplicationStopping.Register(serverLog.Dispose);
}
// Chat mutes and the chat log go through the staff tools when both exist.
if (app.Services.GetService<Aion.Host.Authentication.ServerControl>() is { } serverControl && app.Services.GetService<ChatRelay>() is { } chatRelay)
    chatRelay.Moderation = serverControl;
// Whisper blocks are world data: the auth host's chat asks the world both characters are on.
if (app.Services.GetService<ChatRelay>() is { } chatRelayBlocks)
{
    if (app.Services.GetService<Aion.Host.Worlds.WorldPresenceMirror>() is { } presenceMirror && app.Services.GetService<Aion.Host.Worlds.WorldLinks>() is { } blockLinks)
        chatRelayBlocks.WhisperBlocked = (from, to) => presenceMirror.WorldOf(from) is { } blockWorld
            && blockLinks.BlockedAsync(blockWorld, from, to, default).GetAwaiter().GetResult();
    else if (app.Services.GetService<IGameplayStore>() is { } gameplayStore)
        chatRelayBlocks.WhisperBlocked = (from, to) => gameplayStore.AnyBlockBetweenAsync(from, to, default).GetAwaiter().GetResult();
}
// Behind the declared TLS proxy (loopback), client addresses for rate limits and launch pairing
// come from X-Forwarded-For; only loopback proxies are trusted (the defaults).
if (https.Proxy)
    app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed." });
}));
if (ncGuard.Port is { } ncGuardPort)
{
    var sink = app.Services.GetRequiredService<NcGuardSink>();
    app.Use(async (context, next) =>
    {
        if (context.Connection.LocalPort == ncGuardPort) await sink.HandleAsync(context);
        else await next();
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/internal")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
// Player site and staff dashboard on separate ports (Aion:Web:StaffPort): each port answers only its own site.
if (staffPort is { } siteStaffPort)
    app.UseSiteSplit(port, siteStaffPort, app.Environment.WebRootPath ?? "wwwroot");
if (platform.Port is { } platformPort)
{
    var accountTls = https.Proxy || https.CertificateFile is not null;
    var accountPageBase = accountTls
        ? $"https://{network.PublicAddress}" + (https.Proxy ? "" : $":{port}")
        : $"http://{network.PublicAddress}:{port}";
    app.UsePlatformBootstrap(platformPort, platformResponses!, platform.RequestLog, network, accountPageBase);
}
app.UseRateLimiter();
// Auth host: every /api/admin request needs a staff session (before the world proxy below).
Aion.Host.Authentication.StaffSessionEndpoints.UseStaffSessions(app);
// Auth host: the staff dashboard's world tools (?world=<id>) are answered by that world server.
if (app.Services.GetService<Aion.Host.Worlds.WorldLinks>() is { } worldLinks)
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/admin") && !context.Request.Path.StartsWithSegments("/api/admin/worlds")
            && context.Request.Query["world"] is [{ } worldText]
            && int.TryParse(worldText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var worldId))
            await worldLinks.ProxyAsync(context, worldId);
        else await next();
    });
if (debugOptions.Enabled) app.UsePlaytestHttpLogging();
app.UseWebSockets();
app.MapSynapseStub();
// Without a staff port the staff dashboard can live at a private address on the account port (Aion:Web:StaffPath,
// e.g. "/k3x9..."); then /admin.html is not found. Obscurity only: every staff API call still needs a staff sign-in.
if (staffPort is null && builder.Configuration["Aion:Web:StaffPath"] is { Length: > 1 } staffPath && staffPath.StartsWith('/') && staffPath != "/admin.html")
{
    var dashboardFile = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin.html");
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 404; return; }
        if (path.Equals(staffPath, StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method) && File.Exists(dashboardFile))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(dashboardFile);
            return;
        }
        await next();
    });
}
app.UseDefaultFiles();
app.UseStaticFiles();
// Optional wardrobe export (meshes, textures, icons, vendored three.js) for the staff dashboard's
// preview and icons. Extracted game assets: a public host serves them to signed-in staff only
// (WardrobeAccess cookie); a loopback development host serves them openly unless StaffOnly is set.
var wardrobePath = builder.Configuration["Aion:WardrobePath"];
var wardrobeStaffOnly = !network.LoopbackOnly || builder.Configuration.GetValue<bool>("Aion:Wardrobe:StaffOnly");
if (!string.IsNullOrWhiteSpace(wardrobePath) && Directory.Exists(wardrobePath) && (!wardrobeStaffOnly || accountsReady))
{
    if (wardrobeStaffOnly) WardrobeAccess.Map(app, secure: accountCertificate is not null || https.Proxy);
    var wardrobeRoot = Path.GetFullPath(wardrobePath);
    var wardrobeTypes = new FileExtensionContentTypeProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    { [".glb"] = "model/gltf-binary", [".webp"] = "image/webp", [".json"] = "application/json", [".js"] = "text/javascript" });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wardrobeRoot), RequestPath = "/wardrobe/files", ContentTypeProvider = wardrobeTypes,
        // Exported files do not change between exports; staff browsers keep them for a day.
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "private, max-age=86400",
    });
    app.MapGet("/wardrobe/data", () => Results.File(Path.Combine(wardrobeRoot, "wardrobe.json"), "application/json"));
}
// Player downloads (the launcher, launcher/AionLauncher) from Aion:Web:Downloads, for signed-in accounts.
var downloads = accountsReady && builder.Configuration["Aion:Web:Downloads"] is { Length: > 0 } downloadPath && Directory.Exists(downloadPath)
    ? new Downloads(Path.GetFullPath(downloadPath), TimeProvider.System) : null;
// KiskLauncher's server settings and the client build it checks against (one launcher build plays on any server): on the
// host that serves the platform bootstrap, i.e. the auth host. The account page's download is then a ZIP whose
// launcher.json adds this server.
var launcherEndpoints = accountsReady && platform.Port is not null ? LauncherEndpoints.Embedded() : null;
downloads?.Map(app, launcherEndpoints is null ? null
    : request => LauncherEndpoints.LauncherJson(request, app.Configuration, SiteName(app.Configuration), network.PublicAddress, platform.Port!.Value));
var registrationOpen = builder.Configuration.GetValue<bool?>("Aion:Accounts:Registration") ?? true;
// siteName (Aion:Web:SiteName): the server's name on the player site and the staff dashboard, read per request so a
// changed setting needs no new web build.
app.MapGet("/api/status", () => Results.Ok(new { accountsConfigured = accountsReady, registrationOpen = accountsReady && registrationOpen,
    launcherAvailable = downloads?.LauncherAvailable ?? false, gameReady = false, siteName = SiteName(app.Configuration) }));
launcherEndpoints?.Map(app, () => SiteName(app.Configuration), network.PublicAddress, platform.Port!.Value);
if (accountsReady)
{
    app.MapAccountEndpoints(registrationOpen);
    app.MapTotpEndpoints();
    Aion.Host.Authentication.StaffSessionEndpoints.MapStaffSessionEndpoints(app);
    Aion.Host.Authentication.StaffAccountEndpoints.MapStaffAccountEndpoints(app);
    app.MapGameplayEndpoints();
    Aion.Host.Authentication.StaffEndpoints.MapStaffEndpoints(app);
    Aion.Host.Authentication.StaffToolsEndpoints.MapStaffToolsEndpoints(app);
    Aion.Host.Authentication.StaffToolsEndpoints.MapTicketEndpoints(app);
    Aion.Host.Worlds.WorldEndpoints.MapWorldEndpoints(app);
    Aion.Host.Realm.RealmLinkEndpoints.Map(app);
    var bridgeSecret = builder.Configuration["Aion:BridgeSecret"];
    if (bridgeSecret is { Length: >= 64 })
    {
        app.MapClientHandoff(bridgeSecret);
        app.MapWorldPersistence(bridgeSecret);
    }
}

if (chatPort is { } chatEndpoints) app.MapLimeChat(chatEndpoints, network.PublicAddress);

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
// Do not advertise gameplay readiness before authentication/storage are wired.
app.MapGet("/health/ready", () => Results.Json(
    new { status = "not_ready", reason = "Original-client game-session handoff is not connected." },
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

/// <summary>Aion:Https: PEM certificate and key for the account port, or Proxy when a TLS proxy terminates it.</summary>
sealed record HttpsOptions
{
    public string? CertificateFile { get; init; }
    public string? KeyFile { get; init; }
    public bool Proxy { get; init; }
}
