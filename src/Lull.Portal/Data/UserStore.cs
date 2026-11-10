using Lull.Portal.Auth;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;

namespace Lull.Portal.Data;

public sealed record UserRecord(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool HasPassword,
    bool HasSetupCode,
    DateTimeOffset? SetupCodeExpires,
    int FailedAttempts,
    DateTimeOffset? LockedUntil,
    Guid SecurityStamp,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Students,
    bool IsOwner = false,
    Guid? EntraObjectId = null,
    bool HasTwoFactor = false,
    DateTimeOffset? TwoFactorSince = null)
{
    /// <summary>Signs in with Entra ID: no password or setup code, and its details come from Entra.</summary>
    public bool IsEntra => EntraObjectId is not null;
    /// <summary>
    /// A local admin without two-step sign-in yet: until it's set up, they can only set it up (or sign out).
    /// Entra admins use Entra's own MFA. Parents may not have it yet.
    /// </summary>
    public bool NeedsTwoFactorSetup => Role == Roles.Admin && !IsEntra && !HasTwoFactor;
    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } u && u > now;
    public bool SetupCodeValid(DateTimeOffset now) => HasSetupCode && SetupCodeExpires is { } e && e > now;
}

/// <summary>The views of IT's user list.</summary>
public enum UserFilter
{
    All,
    Parents,
    Admins,
    /// <summary>A local account nobody has set a password for yet, whether or not its setup code has expired.</summary>
    Waiting,
    Locked,
}

public enum SignInOutcome
{
    Ok,
    /// <summary>Unknown email, wrong password or a locked account: the same answer whichever it is.</summary>
    Invalid,
    /// <summary>Locked, told only to someone who has already proved the password (the code step, changing a password).</summary>
    LockedOut,
    /// <summary>Right email, but no password set yet: use the setup code.</summary>
    NeedsSetup,
    /// <summary>The password is right; the account also needs a code from its authenticator app.</summary>
    NeedsSecondFactor,
    /// <summary>The setup code is right, but the password doesn't meet the account's rules (admins need more).</summary>
    WeakPassword,
}

/// <summary>Why a sign-in was refused, for the log (<see cref="Auth.SignInLog"/>). The person is never told which.</summary>
public enum SignInFailure
{
    UnknownAccount,
    /// <summary>The account has no password yet, or signs in with Entra ID.</summary>
    NoPassword,
    WrongPassword,
    /// <summary>A wrong two-step or setup code, or a setup code that has expired or been used.</summary>
    WrongCode,
    /// <summary>The account is locked, so the attempt wasn't considered.</summary>
    Locked,
    /// <summary>The code step's ticket has expired, was tampered with, or the password has changed since.</summary>
    OutdatedTicket,
}

public sealed record SignInResult(
    SignInOutcome Outcome,
    UserRecord? User = null,
    DateTimeOffset? LockedUntil = null,
    string? Problem = null,
    SignInFailure? Failure = null,
    Guid? AccountId = null);

public enum EntraSignInOutcome
{
    Ok,
    /// <summary>
    /// Another account has this email: a local one (not linked, or the owner's) or
    /// another Entra user's (e.g. a reused address). IT resolves it.
    /// </summary>
    Conflict,
    /// <summary>The token has no usable email address.</summary>
    NoEmail,
}

public sealed record EntraSignInResult(EntraSignInOutcome Outcome, UserRecord? User = null);

/// <summary>A change that can't be made, with a message for the admin.</summary>
public sealed class UserChangeException(string message) : Exception(message);

/// <summary>What importing one CSV row did (or would do, in a preview).</summary>
public sealed record ImportResult(UserCsvRow Row, string Action, string? Detail, string? SetupCode = null);

/// <summary>
/// Portal accounts. Parents and admins share one table; parents also have
/// students. Setup codes and passwords are only ever stored hashed, and never
/// written to the audit log.
/// </summary>
public sealed class UserStore(NpgsqlDataSource db, TimeProvider clock, PasswordHasher hasher, IDataProtectionProvider protection)
{
    /// <summary>Two-step keys are stored encrypted with this (the portal's Data Protection keys).</summary>
    readonly IDataProtector twoFactorKeys = protection.CreateProtector("Lull.Portal.TwoFactorKey.v1");

    /// <summary>Failed sign-ins (or setup attempts) before an account is locked for <see cref="LockoutMinutes"/>.</summary>
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;
    public static readonly TimeSpan SetupCodeLifetime = TimeSpan.FromDays(30);

    DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>What <see cref="Read"/> reads, in this order. No secrets: only whether each is set.</summary>
    const string UserColumns = """
        u.id, u.email, u.display_name, u.role, u.password_hash IS NOT NULL, u.setup_code_hash IS NOT NULL,
               u.setup_code_expires, u.failed_attempts, u.locked_until, u.security_stamp, u.created_at, u.last_login_at,
               COALESCE((SELECT array_agg(s.username ORDER BY s.username) FROM user_students s WHERE s.user_id = u.id), '{}'),
               u.is_owner, u.entra_object_id, u.totp_secret IS NOT NULL, u.totp_enabled_at
        """;

    const string SelectUser = "SELECT " + UserColumns + " FROM users u";

    static UserRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<string[]>(12), r.GetBoolean(13),
        r.IsDBNull(14) ? null : r.GetGuid(14), r.GetBoolean(15), r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16));

    async Task<List<UserRecord>> QueryAsync(string where, Action<NpgsqlParameterCollection> bind, CancellationToken ct,
        NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null, string tail = "")
    {
        await using var cmd = conn is null ? db.CreateCommand() : new NpgsqlCommand(null, conn, tx);
        cmd.CommandText = $"{SelectUser} WHERE {where} ORDER BY u.role, u.display_name, u.email {tail}";
        bind(cmd.Parameters);
        var list = new List<UserRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<UserRecord?> GetAsync(Guid id, CancellationToken ct) =>
        (await QueryAsync("u.id = @id", p => p.AddWithValue("id", id), ct)).FirstOrDefault();

    public async Task<UserRecord?> FindByEmailAsync(string email, CancellationToken ct) =>
        (await QueryAsync("u.email = @e", p => p.AddWithValue("e", UserCsv.NormalizeEmail(email)), ct)).FirstOrDefault();

    public Task<List<UserRecord>> ListAsync(CancellationToken ct) => QueryAsync("true", _ => { }, ct);

    static string FilterSql(UserFilter filter) => filter switch
    {
        UserFilter.Parents => "u.role = 'Parent'",
        UserFilter.Admins => "u.role = 'Admin'",
        UserFilter.Waiting => "(u.password_hash IS NULL AND u.entra_object_id IS NULL)",
        UserFilter.Locked => "u.locked_until > @now",
        _ => "true",
    };

    /// <summary>
    /// IT's user list: one page of the accounts whose name, email or one of whose students contains
    /// <paramref name="query"/> (any case; all of them when empty) and that <paramref name="filter"/>
    /// keeps, and how many that search finds for each filter.
    /// </summary>
    public async Task<(Paged<UserRecord> Page, IReadOnlyDictionary<UserFilter, int> Counts)> SearchAsync(
        string? query, UserFilter filter, int? page, CancellationToken ct)
    {
        const string search = """
            (@q = '' OR strpos(lower(u.email), @q) > 0 OR strpos(lower(u.display_name), @q) > 0
                OR EXISTS (SELECT 1 FROM user_students s WHERE s.user_id = u.id AND strpos(lower(s.username), @q) > 0))
            """;
        var q = Paging.Normalize(query);
        var now = Now;
        void Bind(NpgsqlParameterCollection p)
        {
            p.AddWithValue("q", q);
            p.AddWithValue("now", now);
        }

        var filters = Enum.GetValues<UserFilter>();
        var columns = string.Join(", ", filters.Select(f => $"count(*) FILTER (WHERE {FilterSql(f)})"));
        await using var count = db.CreateCommand($"SELECT {columns} FROM users u WHERE {search}");
        Bind(count.Parameters);
        var counts = new Dictionary<UserFilter, int>();
        await using (var r = await count.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            for (int i = 0; i < filters.Length; i++) counts[filters[i]] = (int)r.GetInt64(i);
        }

        var number = Paging.Clamp(page, counts[filter]);
        var rows = await QueryAsync($"{search} AND {FilterSql(filter)}", p =>
        {
            Bind(p);
            p.AddWithValue("offset", Paging.Offset(number));
            p.AddWithValue("limit", Paging.Size);
        }, ct, tail: "OFFSET @offset LIMIT @limit");
        return (new(rows, number, counts[filter]), counts);
    }

    public async Task<IReadOnlyList<string>> GetStudentsAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT username FROM user_students WHERE user_id = @id ORDER BY username");
        cmd.Parameters.AddWithValue("id", userId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    // MARK: First run

    // Once an admin exists there's always one (the owner can't be removed), so
    // this only ever goes false -> true and can be cached.
    volatile bool hasAdmin;

    /// <summary>Whether the portal has been set up (an admin exists). Cheap after the first true.</summary>
    public async Task<bool> HasAdminAsync(CancellationToken ct)
    {
        if (hasAdmin) return true;
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM users WHERE role = 'Admin')");
        return hasAdmin = (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// First run: the first person to open the portal (IT, deploying it)
    /// creates the owner, the first admin. Under an advisory lock so two
    /// visitors can't both become owner. Null if the portal is already set up.
    /// </summary>
    public async Task<UserRecord?> CreateOwnerAsync(string email, string name, string password, CancellationToken ct)
    {
        var normalized = Validate(email, name, Roles.Admin);
        if (PasswordRules.Problem(Roles.Admin, password) is { } problem) throw new UserChangeException(problem);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(0x4c756c6d)", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);
        await using (var any = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users WHERE role = 'Admin')", conn, tx))
            if ((bool)(await any.ExecuteScalarAsync(ct))!)
            {
                hasAdmin = true;
                return null;
            }
        var id = Guid.NewGuid();
        // A parent account with the same email can't exist yet in practice (only admins create users).
        await InsertAsync(conn, tx, id, normalized, name.Trim(), Roles.Admin, hasher.Hash(password), null, ct);
        await using (var owner = new NpgsqlCommand("UPDATE users SET is_owner = true, last_login_at = @now WHERE id = @id", conn, tx))
        {
            owner.Parameters.AddWithValue("now", Now);
            owner.Parameters.AddWithValue("id", id);
            await owner.ExecuteNonQueryAsync(ct);
        }
        await PortalStore.WriteAuditAsync(conn, tx, Now, normalized, null, "portal_set_up", new { email = normalized, name = name.Trim() }, ct);
        await tx.CommitAsync(ct);
        hasAdmin = true;
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Recovery when the owner has lost their password: run on the server
    /// (<c>dotnet Lull.Portal.dll --reset-owner</c>). A new setup code for the
    /// owner, used on the portal's "Set up your password" page. Null if there's no owner.
    /// </summary>
    public async Task<(string Email, string Code)?> ResetOwnerAsync(CancellationToken ct)
    {
        var owner = (await QueryAsync("u.is_owner", _ => { }, ct)).FirstOrDefault();
        if (owner is null) return null;
        return (owner.Email, await NewSetupCodeAsync(owner.Id, owner.Id, "server (--reset-owner)", ct));
    }

    // MARK: Signing in

    /// <summary>
    /// Email and password. Anything but the right password on an unlocked account gets
    /// <see cref="SignInOutcome.Invalid"/>, so the answer never says which emails have accounts.
    /// That includes the right password on a locked account: if it answered differently,
    /// guessing could carry on through the lockout.
    /// </summary>
    /// <param name="ownerOnly">Local sign-in is off: only the owner (the emergency admin) may use a password.</param>
    public async Task<SignInResult> SignInAsync(string? email, string? password, CancellationToken ct, bool ownerOnly = false)
    {
        var (user, hash) = await CredentialsAsync(email, ct);
        if (ownerOnly && user is { IsOwner: false }) (user, hash) = (null, null);
        if (user is null || hash is null)
        {
            hasher.Waste(password ?? "");
            return user is null
                ? Refused(SignInFailure.UnknownAccount)
                : new(SignInOutcome.NeedsSetup, user, Failure: SignInFailure.NoPassword, AccountId: user.Id);
        }
        // Worked out even for a locked account, so that it takes as long to answer as any other.
        var right = PasswordHasher.Verify(password ?? "", hash);
        if (user.IsLocked(Now)) return Refused(SignInFailure.Locked, user);
        if (!right)
        {
            await FailedAsync(user, SignInFailure.WrongPassword, ct);
            return Refused(SignInFailure.WrongPassword, user);
        }
        // Failed attempts are cleared only once the code is right too, so the password can't reset the count of wrong codes.
        if (user.HasTwoFactor) return new(SignInOutcome.NeedsSecondFactor, user);
        await SucceededAsync(user.Id, ct);
        return new(SignInOutcome.Ok, user);
    }

    /// <summary>
    /// The second step for an account with two-step sign-in, after <see cref="SignInAsync"/> said
    /// <see cref="SignInOutcome.NeedsSecondFactor"/>. <paramref name="stamp"/> is the account's
    /// security stamp then: a password reset in between makes the attempt invalid. Wrong codes count
    /// towards the lockout like wrong passwords, and each code works only once.
    /// </summary>
    public async Task<SignInResult> VerifySecondFactorAsync(Guid id, Guid stamp, string? code, CancellationToken ct)
    {
        var user = await GetAsync(id, ct);
        if (user is null || user.SecurityStamp != stamp || !user.HasTwoFactor) return Refused(SignInFailure.OutdatedTicket, user);
        if (user.IsLocked(Now)) return new(SignInOutcome.LockedOut, LockedUntil: user.LockedUntil, Failure: SignInFailure.Locked, AccountId: user.Id);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? stored;
        long? lastStep;
        await using (var read = new NpgsqlCommand("SELECT totp_secret, totp_last_step FROM users WHERE id = @id FOR UPDATE", conn, tx))
        {
            read.Parameters.AddWithValue("id", id);
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Refused(SignInFailure.OutdatedTicket, user);
            stored = r.IsDBNull(0) ? null : r.GetString(0);
            lastStep = r.IsDBNull(1) ? null : r.GetInt64(1);
        }
        var step = stored is null ? null : Totp.Match(Unprotect(stored), code, Now);
        if (step is null || step <= lastStep)
        {
            await tx.RollbackAsync(ct);
            return await FailedAsync(user, SignInFailure.WrongCode, ct);
        }
        await using (var used = new NpgsqlCommand(
            "UPDATE users SET totp_last_step = @step, failed_attempts = 0, locked_until = NULL, last_login_at = @now WHERE id = @id", conn, tx))
        {
            used.Parameters.AddWithValue("step", step.Value);
            used.Parameters.AddWithValue("now", Now);
            used.Parameters.AddWithValue("id", id);
            await used.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(SignInOutcome.Ok, user);
    }

    /// <summary>
    /// Turns on two-step sign-in with <paramref name="key"/>, once <paramref name="code"/> from the
    /// authenticator app shows it was added there. Ends the account's other sessions. Null if the
    /// code is wrong, or the account can't have it (Entra) or already has it.
    /// </summary>
    public async Task<UserRecord?> EnableTwoFactorAsync(Guid id, byte[] key, string? code, CancellationToken ct)
    {
        var user = await GetAsync(id, ct);
        if (user is null || user.IsEntra || user.HasTwoFactor || Totp.Match(key, code, Now) is not { } step) return null;
        await ExecAsync(user.Email, id, "two_factor_enabled", null, ct,
            """
            UPDATE users SET totp_secret = @k, totp_last_step = @step, totp_enabled_at = @now, security_stamp = @stamp, updated_at = @now
            WHERE id = @id AND totp_secret IS NULL
            """,
            p =>
            {
                p.AddWithValue("k", twoFactorKeys.Protect(Convert.ToBase64String(key)));
                p.AddWithValue("step", step);
                p.AddWithValue("stamp", Guid.NewGuid());
            });
        return await GetAsync(id, ct);
    }

    byte[] Unprotect(string stored) => Convert.FromBase64String(twoFactorKeys.Unprotect(stored));

    /// <summary>
    /// First sign-in: email + one-time setup code, then the new password. As with
    /// <see cref="SignInAsync"/>, a locked account gets the same answer even with the right code,
    /// and the account's own password rules (stricter for admins) are checked only once the code is right.
    /// </summary>
    /// <param name="checkRules">False only for tests that set up an account with a password from before the current rules.</param>
    public async Task<SignInResult> CompleteSetupAsync(
        string? email, string? code, string newPassword, CancellationToken ct, bool ownerOnly = false, bool checkRules = true)
    {
        var user = await FindByEmailAsync(email ?? "", ct);
        if (ownerOnly && user is { IsOwner: false }) user = null;
        if (user is null)
        {
            hasher.Waste(code ?? "");
            return Refused(SignInFailure.UnknownAccount);
        }
        var stored = await SetupHashAsync(user.Id, ct);
        var right = user.SetupCodeValid(Now) && SetupCodes.Matches(code, stored);
        if (user.IsLocked(Now)) return Refused(SignInFailure.Locked, user);
        if (!right)
        {
            await FailedAsync(user, SignInFailure.WrongCode, ct);
            return Refused(SignInFailure.WrongCode, user);
        }
        if (checkRules && PasswordRules.Problem(user.Role, newPassword) is { } problem) return new(SignInOutcome.WeakPassword, Problem: problem);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE users SET password_hash = @p, setup_code_hash = NULL, setup_code_expires = NULL, failed_attempts = 0,
                locked_until = NULL, last_login_at = @now, updated_at = @now
            WHERE id = @id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("p", hasher.Hash(newPassword));
            cmd.Parameters.AddWithValue("now", Now);
            cmd.Parameters.AddWithValue("id", user.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await PortalStore.WriteAuditAsync(conn, tx, Now, user.Email, null, "password_set", null, ct);
        await tx.CommitAsync(ct);
        return new(SignInOutcome.Ok, user);
    }

    /// <summary>
    /// Signed-in user changes their own password (needs the current one). Ends their other
    /// sessions: the caller signs this one in again. A wrong current password counts towards the
    /// lockout, and a locked account can't change it.
    /// </summary>
    public async Task<SignInResult> ChangePasswordAsync(Guid id, string current, string newPassword, CancellationToken ct)
    {
        var (user, hash) = await CredentialsAsync(id, ct);
        if (user is null) return Refused(SignInFailure.UnknownAccount);
        if (user.IsLocked(Now)) return new(SignInOutcome.LockedOut, LockedUntil: user.LockedUntil, Failure: SignInFailure.Locked, AccountId: user.Id);
        if (!PasswordHasher.Verify(current, hash)) return await FailedAsync(user, SignInFailure.WrongPassword, ct);
        await ExecAsync(user.Email, user.Id, "password_changed", null, ct,
            "UPDATE users SET password_hash = @p, security_stamp = @stamp, updated_at = @now WHERE id = @id",
            p =>
            {
                p.AddWithValue("p", hasher.Hash(newPassword));
                p.AddWithValue("stamp", Guid.NewGuid());
            });
        return new(SignInOutcome.Ok, await GetAsync(id, ct));
    }

    /// <summary>The account with this email and its password hash, in one query (the hash never leaves this class).</summary>
    Task<(UserRecord? User, string? Hash)> CredentialsAsync(string? email, CancellationToken ct) =>
        CredentialsAsync("u.email = @e", p => p.AddWithValue("e", UserCsv.NormalizeEmail(email ?? "")), ct);

    Task<(UserRecord? User, string? Hash)> CredentialsAsync(Guid id, CancellationToken ct) =>
        CredentialsAsync("u.id = @id", p => p.AddWithValue("id", id), ct);

    async Task<(UserRecord? User, string? Hash)> CredentialsAsync(string where, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {UserColumns}, u.password_hash FROM users u WHERE {where}");
        bind(cmd.Parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        var hash = r.GetOrdinal("password_hash") is var at && !r.IsDBNull(at) ? r.GetString(at) : null;
        return (Read(r), hash);
    }

    async Task<byte[]?> SetupHashAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT setup_code_hash FROM users WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    static SignInResult Refused(SignInFailure why, UserRecord? user = null) =>
        new(SignInOutcome.Invalid, Failure: why, AccountId: user?.Id);

    async Task<SignInResult> FailedAsync(UserRecord user, SignInFailure why, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE users SET failed_attempts = failed_attempts + 1,
                locked_until = CASE WHEN failed_attempts + 1 >= @max THEN @until ELSE locked_until END
            WHERE id = @id
            RETURNING locked_until
            """);
        cmd.Parameters.AddWithValue("max", MaxFailedAttempts);
        cmd.Parameters.AddWithValue("until", Now.AddMinutes(LockoutMinutes));
        cmd.Parameters.AddWithValue("id", user.Id);
        DateTimeOffset? locked = null;
        // A typed read: ExecuteScalar would hand back timestamptz as DateTime, not DateTimeOffset.
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct) && !r.IsDBNull(0) && r.GetFieldValue<DateTimeOffset>(0) is var l && l > Now) locked = l;
        if (locked is not null)
        {
            // A lockout is worth an audit entry; single failures aren't (they'd flood it), and go to the log (SignInLog).
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await PortalStore.WriteAuditAsync(conn, tx, Now, "system", null, "account_locked", new { email = user.Email }, ct);
            await tx.CommitAsync(ct);
            return new(SignInOutcome.LockedOut, LockedUntil: locked, Failure: why, AccountId: user.Id);
        }
        return Refused(why, user);
    }

    async Task SucceededAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE users SET failed_attempts = 0, locked_until = NULL, last_login_at = @now WHERE id = @id");
        cmd.Parameters.AddWithValue("now", Now);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // MARK: Signing in with Entra ID

    public const string EntraManaged =
        "This account signs in with Entra ID: its name, email, role and students come from Entra each time they sign in.";

    /// <summary>
    /// After Entra ID has signed someone in: finds their account by Entra object ID
    /// and brings it up to date from the token, or creates it. A local account with
    /// the same email is taken over only when <paramref name="linkByEmail"/> is on,
    /// and never the owner's (the emergency admin always keeps its password).
    /// </summary>
    public async Task<EntraSignInResult> SignInEntraAsync(EntraIdentity who, bool linkByEmail, CancellationToken ct)
    {
        var email = UserCsv.NormalizeEmail(who.Email ?? "");
        if (!UserCsv.IsEmail(email)) return new(EntraSignInOutcome.NoEmail);
        var name = string.IsNullOrWhiteSpace(who.Name) ? email : who.Name.Trim();
        if (name.Length > 200) name = name[..200];
        var students = who.Role == Roles.Parent ? who.Students.Select(ChildList.Normalize).Distinct().ToList() : [];

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = await QueryAsync("(u.entra_object_id = @oid OR u.email = @e)", p =>
        {
            p.AddWithValue("oid", who.ObjectId);
            p.AddWithValue("e", email);
        }, ct, conn, tx, tail: "FOR UPDATE OF u");
        var mine = rows.FirstOrDefault(u => u.EntraObjectId == who.ObjectId);
        bool linking = false;
        if (rows.FirstOrDefault(u => u.Email == email && u.EntraObjectId != who.ObjectId) is { } other)
        {
            if (mine is not null || other.IsEntra || other.IsOwner || !linkByEmail) return new(EntraSignInOutcome.Conflict);
            (mine, linking) = (other, true);
        }

        if (mine is null)
        {
            var id = Guid.NewGuid();
            await InsertAsync(conn, tx, id, email, name, who.Role, null, null, ct);
            await using (var cmd = new NpgsqlCommand("UPDATE users SET entra_object_id = @oid, last_login_at = @now WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("oid", who.ObjectId);
                cmd.Parameters.AddWithValue("now", Now);
                cmd.Parameters.AddWithValue("id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await SetStudentsAsync(conn, tx, id, students, ct);
            await PortalStore.WriteAuditAsync(conn, tx, Now, email, null, "user_created",
                new { email, name, role = who.Role, students, source = "entra" }, ct);
            await tx.CommitAsync(ct);
            return new(EntraSignInOutcome.Ok, await GetAsync(id, ct));
        }

        // As when IT edits a local account: a new username or role ends other sessions.
        // Taking over a local account ends its password sessions too.
        bool endSessions = mine.Email != email || mine.Role != who.Role || linking;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE users SET entra_object_id = @oid, email = @e, display_name = @n, role = @r, password_hash = NULL,
                setup_code_hash = NULL, setup_code_expires = NULL, failed_attempts = 0, locked_until = NULL,
                totp_secret = NULL, totp_last_step = NULL, totp_enabled_at = NULL,
                last_login_at = @now, updated_at = @now {(endSessions ? ", security_stamp = @stamp" : "")}
            WHERE id = @id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("oid", who.ObjectId);
            cmd.Parameters.AddWithValue("e", email);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("r", who.Role);
            cmd.Parameters.AddWithValue("now", Now);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            cmd.Parameters.AddWithValue("id", mine.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await SetStudentsAsync(conn, tx, mine.Id, students, ct);
        if (linking) await PortalStore.WriteAuditAsync(conn, tx, Now, email, null, "user_linked_to_entra", new { email }, ct);
        if (mine.Email != email || mine.DisplayName != name || mine.Role != who.Role || !mine.Students.ToHashSet().SetEquals(students))
            await PortalStore.WriteAuditAsync(conn, tx, Now, email, null, "user_updated",
                new
                {
                    email, previousEmail = mine.Email != email ? mine.Email : null, name, role = who.Role,
                    students, previousStudents = mine.Students, source = "entra",
                }, ct);
        await tx.CommitAsync(ct);
        return new(EntraSignInOutcome.Ok, await GetAsync(mine.Id, ct));
    }

    // MARK: Managing users (admins)

    /// <summary>Creates a user with a new one-time setup code, which is returned (and shown to the admin) once.</summary>
    public async Task<(UserRecord User, string SetupCode)> CreateAsync(
        string email, string name, string role, IReadOnlyList<string> students, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (id, code) = await CreateInTxAsync(conn, tx, email, name, role, students, actor, ct);
        await tx.CommitAsync(ct);
        return ((await GetAsync(id, ct))!, code);
    }

    async Task<(Guid Id, string Code)> CreateInTxAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string email, string name, string role, IReadOnlyList<string> students,
        string actor, CancellationToken ct)
    {
        var normalized = Validate(email, name, role);
        var id = Guid.NewGuid();
        var code = SetupCodes.New();
        try
        {
            await InsertAsync(conn, tx, id, normalized, name.Trim(), role, null, code, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UserChangeException($"{normalized} already has an account.");
        }
        await SetStudentsAsync(conn, tx, id, role == Roles.Parent ? students : [], ct);
        await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, "user_created",
            new { email = normalized, name = name.Trim(), role, students }, ct);
        return (id, code);
    }

    public async Task UpdateAsync(
        Guid id, string email, string name, string role, IReadOnlyList<string> students, Guid actingUser, string actor,
        CancellationToken ct)
    {
        var normalized = Validate(email, name, role);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var before = (await QueryAsync("u.id = @id", p => p.AddWithValue("id", id), ct, conn, tx)).FirstOrDefault()
            ?? throw new UserChangeException("That account no longer exists.");
        if (before.IsEntra) throw new UserChangeException(EntraManaged);
        if (before.IsOwner && actingUser != id) throw new UserChangeException("Only the owner can change the owner account.");
        if (before.IsOwner && role != Roles.Admin) throw new UserChangeException("The owner account is always an admin.");
        if (before.Role == Roles.Admin && role != Roles.Admin) await EnsureAnotherAdminAsync(conn, tx, id, ct);
        // A new username or role ends existing sessions (the cookie carries both).
        bool endSessions = before.Email != normalized || before.Role != role;
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"""
                UPDATE users SET email = @e, display_name = @n, role = @r, updated_at = @now
                    {(endSessions ? ", security_stamp = @stamp" : "")}
                WHERE id = @id
                """, conn, tx);
            cmd.Parameters.AddWithValue("e", normalized);
            cmd.Parameters.AddWithValue("n", name.Trim());
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("now", Now);
            cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UserChangeException($"{normalized} already has an account.");
        }
        await SetStudentsAsync(conn, tx, id, role == Roles.Parent ? students : [], ct);
        await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, "user_updated",
            new
            {
                email = normalized, previousEmail = before.Email != normalized ? before.Email : null, name = name.Trim(), role,
                students = role == Roles.Parent ? students : [], previousStudents = before.Students,
            }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(Guid id, Guid actingUser, string actor, CancellationToken ct)
    {
        if (id == actingUser) throw new UserChangeException("You can't delete your own account.");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var user = (await QueryAsync("u.id = @id", p => p.AddWithValue("id", id), ct, conn, tx)).FirstOrDefault()
            ?? throw new UserChangeException("That account no longer exists.");
        if (user.IsOwner) throw new UserChangeException("The owner account (the portal's first admin) can't be deleted.");
        if (user.Role == Roles.Admin) await EnsureAnotherAdminAsync(conn, tx, id, ct);
        await using (var cmd = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, "user_deleted",
            new { email = user.Email, name = user.DisplayName, role = user.Role, students = user.Students }, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// New one-time setup code: clears the password and two-step sign-in, ends every session and
    /// clears a lockout, so the person sets a new password with the code (and an admin sets up
    /// two-step sign-in again: the way back from a lost phone). Returned once.
    /// </summary>
    public async Task<string> NewSetupCodeAsync(Guid id, Guid actingUser, string actor, CancellationToken ct)
    {
        var user = await GetAsync(id, ct);
        // A password would let it sign in without Entra.
        if (user?.IsEntra == true) throw new UserChangeException(EntraManaged);
        // Another admin issuing the owner a code could take the account over.
        if (id != actingUser && user?.IsOwner == true)
            throw new UserChangeException("Only the owner can reset the owner's password (or someone with server access: --reset-owner).");
        var code = SetupCodes.New();
        await ExecAsync(actor, id, "setup_code_issued", null, ct,
            """
            UPDATE users SET password_hash = NULL, setup_code_hash = @h, setup_code_expires = @exp, failed_attempts = 0,
                locked_until = NULL, totp_secret = NULL, totp_last_step = NULL, totp_enabled_at = NULL,
                security_stamp = @stamp, updated_at = @now
            WHERE id = @id
            """,
            p =>
            {
                p.AddWithValue("h", SetupCodes.Hash(code));
                p.AddWithValue("exp", Now + SetupCodeLifetime);
                p.AddWithValue("stamp", Guid.NewGuid());
            });
        return code;
    }

    public Task UnlockAsync(Guid id, string actor, CancellationToken ct) =>
        ExecAsync(actor, id, "account_unlocked", null, ct,
            "UPDATE users SET failed_attempts = 0, locked_until = NULL, updated_at = @now WHERE id = @id", _ => { });

    // MARK: CSV import

    /// <summary>
    /// What importing <paramref name="rows"/> would do, without changing anything
    /// (<paramref name="apply"/> false), or does it. The file is the source of
    /// truth for the parents it lists: name and students are replaced. Parents
    /// not in the file are left alone. Setup codes are returned for new parents only.
    /// </summary>
    public async Task<IReadOnlyList<ImportResult>> ImportAsync(
        IReadOnlyList<UserCsvRow> rows, bool apply, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var existing = (await QueryAsync("true", _ => { }, ct, conn, tx)).ToDictionary(u => u.Email);
        var results = new List<ImportResult>();
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Email, out var user))
            {
                string? code = null;
                if (apply) (_, code) = await CreateInTxAsync(conn, tx, row.Email, row.Name, Roles.Parent, row.Students, actor, ct);
                results.Add(new(row, "Create", Students(row.Students), code));
                continue;
            }
            if (user.IsEntra)
            {
                results.Add(new(row, "Skip", $"{row.Email} signs in with Entra ID; its students come from Entra"));
                continue;
            }
            if (user.Role != Roles.Parent)
            {
                results.Add(new(row, "Skip", $"{row.Email} is an admin account; not changed by imports"));
                continue;
            }
            var changes = new List<string>();
            if (user.DisplayName != row.Name) changes.Add($"name \"{user.DisplayName}\" → \"{row.Name}\"");
            if (!user.Students.Order().SequenceEqual(row.Students.Order()))
                changes.Add($"students {Students(user.Students)} → {Students(row.Students)}");
            if (changes.Count == 0)
            {
                results.Add(new(row, "Unchanged", null));
                continue;
            }
            if (apply)
            {
                await using (var cmd = new NpgsqlCommand("UPDATE users SET display_name = @n, updated_at = @now WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("n", row.Name);
                    cmd.Parameters.AddWithValue("now", Now);
                    cmd.Parameters.AddWithValue("id", user.Id);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await SetStudentsAsync(conn, tx, user.Id, row.Students, ct);
                await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, "user_updated",
                    new { email = row.Email, name = row.Name, students = row.Students, previousStudents = user.Students, source = "import" }, ct);
            }
            results.Add(new(row, "Update", string.Join("; ", changes)));
        }
        if (apply)
        {
            await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, "users_imported",
                new
                {
                    rows = rows.Count, created = results.Count(r => r.Action == "Create"),
                    updated = results.Count(r => r.Action == "Update"),
                }, ct);
            await tx.CommitAsync(ct);
        }
        return results;
    }

    static string Students(IReadOnlyList<string> s) => s.Count == 0 ? "(none)" : string.Join(", ", s);

    // MARK: Helpers

    static string Validate(string email, string name, string role)
    {
        var normalized = UserCsv.NormalizeEmail(email);
        if (!UserCsv.IsEmail(normalized)) throw new UserChangeException("Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new UserChangeException("Enter a name (up to 200 characters).");
        if (!Roles.IsValid(role)) throw new UserChangeException("Choose Parent or Admin.");
        return normalized;
    }

    async Task InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string email, string name, string role, string? passwordHash,
        string? setupCode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, role, password_hash, setup_code_hash, setup_code_expires,
                security_stamp, created_at, updated_at)
            VALUES (@id, @e, @n, @r, @p, @h, @exp, @stamp, @now, @now)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)passwordHash ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Bytea)
        {
            Value = setupCode is null ? DBNull.Value : SetupCodes.Hash(setupCode),
        });
        cmd.Parameters.Add(new NpgsqlParameter("exp", NpgsqlDbType.TimestampTz)
        {
            Value = setupCode is null ? DBNull.Value : Now + SetupCodeLifetime,
        });
        cmd.Parameters.AddWithValue("stamp", Guid.NewGuid());
        cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task SetStudentsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, IReadOnlyList<string> students, CancellationToken ct)
    {
        await using (var del = new NpgsqlCommand("DELETE FROM user_students WHERE user_id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync(ct);
        }
        if (students.Count == 0) return;
        await using var ins = new NpgsqlCommand(
            "INSERT INTO user_students (user_id, username) SELECT @id, unnest(@names) ON CONFLICT DO NOTHING", conn, tx);
        ins.Parameters.AddWithValue("id", id);
        ins.Parameters.AddWithValue("names", students.Select(ChildList.Normalize).Distinct().ToArray());
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The portal must always keep an admin, or nobody can manage it.</summary>
    static async Task EnsureAnotherAdminAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        // FOR UPDATE: two admins removing each other at once can't leave none.
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM (SELECT 1 FROM users WHERE role = 'Admin' AND id <> @id FOR UPDATE) a", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        if ((long)(await cmd.ExecuteScalarAsync(ct))! == 0)
            throw new UserChangeException("This is the last admin account. Create another admin first.");
    }

    async Task ExecAsync(
        string actor, Guid id, string action, object? detail, CancellationToken ct, string sql, Action<NpgsqlParameterCollection> bind)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string email;
        await using (var cmd = new NpgsqlCommand(sql + " RETURNING email", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("now", Now);
            bind(cmd.Parameters);
            email = await cmd.ExecuteScalarAsync(ct) as string ?? throw new UserChangeException("That account no longer exists.");
        }
        await PortalStore.WriteAuditAsync(conn, tx, Now, actor, null, action, detail ?? new { email }, ct);
        await tx.CommitAsync(ct);
    }
}
