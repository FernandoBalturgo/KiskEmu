using Aion.Application.Authentication;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// Staff dashboard sign-in (IStaffSessions): the password, a staff role and an authenticator code give a staff session
/// (auth.sessions.kind 'staff'). Staff without an authenticator set one up inside the sign-in ('staff_enroll' challenge).
/// </summary>
public sealed partial class PostgresAccountAuthentication : IStaffSessions
{
    /// <summary>An enrolment challenge allows time to scan the key and type the first code.</summary>
    static readonly TimeSpan EnrollChallengeLifetime = TimeSpan.FromMinutes(15), CodeChallengeLifetime = TimeSpan.FromMinutes(5);

    static async Task<bool> IsStaffAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid account, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand("SELECT 1 FROM auth.accounts a JOIN auth.account_roles r USING(account_id) "
            + "WHERE a.account_id=$1 AND a.disabled_at IS NULL AND r.role IN (" + StaffAccess.RoleSqlList + ") LIMIT 1", connection, transaction);
        query.Parameters.AddWithValue(account);
        return await query.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<StaffSignIn> BeginAsync(string username, string password, bool remember, CancellationToken ct)
    {
        // The password step of the account sign-in (lockout, rehash, disabled accounts); its session is not kept.
        var checkedPassword = await SignInAsync(username, password, DefaultSessionLifetime, ct);
        if (checkedPassword is null) return new(StaffSignInStep.Failed);
        await SignOutAsync(checkedPassword.Token, ct);
        var account = checkedPassword.AccountId;
        await using (var connection = await source.OpenConnectionAsync(ct))
            if (!await IsStaffAsync(connection, null, account, ct)) return new(StaffSignInStep.NotStaff);
        var lifetime = remember ? IStaffSessions.RememberedLifetime : IStaffSessions.Lifetime;
        var enrolled = TotpEnabled ? await IsEnabledAsync(account, ct) : true;
        var challenge = await BeginChallengeAsync(account, lifetime, enrolled ? ChallengeStaff : ChallengeStaffEnroll,
            enrolled ? CodeChallengeLifetime : EnrollChallengeLifetime, ct);
        return challenge is null ? new(StaffSignInStep.Failed)
            : new(enrolled ? StaffSignInStep.Code : StaffSignInStep.Enroll, challenge.ChallengeToken, challenge.ExpiresAt);
    }

    public Task<AccountSession?> CompleteAsync(string challengeToken, string? code, string? recoveryCode, CancellationToken ct) =>
        CompleteChallengeAsync(challengeToken, code, recoveryCode, ChallengeStaff, ct);

    /// <summary>The account and session length of a live enrolment challenge.</summary>
    async Task<(Guid Account, TimeSpan Lifetime)?> EnrollChallengeAsync(string challengeToken, CancellationToken ct)
    {
        if (!ValidToken(challengeToken)) return null;
        await using var query = source.CreateCommand("""
            SELECT account_id, session_seconds FROM auth.totp_login_challenges
            WHERE token_hash=$1 AND expires_at>now() AND purpose='staff_enroll' AND (locked_until IS NULL OR locked_until<=now())
            """);
        query.Parameters.AddWithValue(Hash(challengeToken));
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetGuid(0), reader.IsDBNull(1) ? IStaffSessions.Lifetime : TimeSpan.FromSeconds(reader.GetInt32(1)));
    }

    public async Task<TotpEnrollment?> BeginEnrollmentAsync(string challengeToken, CancellationToken ct)
    {
        if (await EnrollChallengeAsync(challengeToken, ct) is not { } challenge) return null;
        var username = await LookupUsernameAsync(challenge.Account, ct) ?? challenge.Account.ToString("N")[..8];
        return await BeginEnrollmentAsync(challenge.Account, username, ct);
    }

    public async Task<(AccountSession Session, string[] RecoveryCodes)?> ConfirmEnrollmentAsync(string challengeToken, string code, CancellationToken ct)
    {
        if (await EnrollChallengeAsync(challengeToken, ct) is not { } challenge) return null;
        var (ok, recovery) = await ConfirmEnrollmentAsync(challenge.Account, code, ct);
        if (!ok)
        {
            await using var fail = source.CreateCommand("""
                UPDATE auth.totp_login_challenges SET
                  failed_attempts=CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END,
                  locked_until=CASE WHEN (CASE WHEN locked_until<=now() THEN 1 ELSE failed_attempts+1 END)>=5
                    THEN now()+interval '5 minutes' ELSE NULL END
                WHERE token_hash=$1
                """);
            fail.Parameters.AddWithValue(Hash(challengeToken));
            await fail.ExecuteNonQueryAsync(ct);
            return null;
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var done = new NpgsqlCommand("DELETE FROM auth.totp_login_challenges WHERE token_hash=$1 AND purpose='staff_enroll'", connection, transaction))
        {
            done.Parameters.AddWithValue(Hash(challengeToken));
            if (await done.ExecuteNonQueryAsync(ct) == 0) return null;   // used by a parallel request
        }
        if (!await IsStaffAsync(connection, transaction, challenge.Account, ct)) return null;
        var session = await InsertSessionAsync(connection, transaction, challenge.Account, challenge.Lifetime, SessionStaff, ct);
        await transaction.CommitAsync(ct);
        return (session, recovery ?? []);
    }

    async Task<VerifiedAccount?> IStaffSessions.AuthenticateAsync(string token, CancellationToken ct)
    {
        if (!ValidToken(token)) return null;
        await using var command = source.CreateCommand("SELECT s.account_id FROM auth.sessions s JOIN auth.accounts a USING(account_id) "
            + "WHERE s.token_hash=$1 AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL "
            + "AND EXISTS (SELECT 1 FROM auth.account_roles r WHERE r.account_id=s.account_id AND r.role IN (" + StaffAccess.RoleSqlList + "))");
        command.Parameters.AddWithValue(Hash(token));
        return await command.ExecuteScalarAsync(ct) is Guid account ? new(account) : null;
    }
}
