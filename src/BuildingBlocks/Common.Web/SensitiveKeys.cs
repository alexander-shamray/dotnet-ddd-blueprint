namespace Common.Web;

/// <summary>
/// The one never-log vocabulary (§13.4), declared once because two things read
/// it: <see cref="SensitiveDataRedactor"/> for a record's attributes, and
/// <see cref="RedactingScopeProvider"/> for the scopes those records inherit.
/// A second copy is a second specification, and the copy nobody edits is the
/// one that stops matching.
/// </summary>
/// <remarks>
/// <b>Public so a test can pin it.</b> The list is the control, so a term
/// removed in a refactor has to fail a test rather than silently widen what is
/// exported — the same argument that makes <see cref="SensitiveDataRedactor"/>
/// public.
/// <para>
/// <b>Matching is by substring, ordinal and case-insensitive.</b> The field
/// that leaks is never named exactly <c>password</c> — it is
/// <c>NewPassword</c>, <c>card_number</c>, <c>id_token</c>. The cost is that a
/// term which is a substring of an innocent word redacts that word too, which
/// is why <c>pin</c> is deliberately absent: <c>Shipping</c> contains it.
/// </para>
/// </remarks>
public static class SensitiveKeys
{
    // Both spellings of the snake_case entries are listed rather than
    // normalised, because normalising a key would have to guess at the
    // separator and a miss here is silent.
    private static readonly string[] Terms =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "authorization",
        "credential",
        "cookie",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "privatekey",
        "private_key",
        "cardnumber",
        "card_number",
        "ssn",
        "nationalid",
        "cvv",
        "otp",
        "sessionid",
        "session_id",
        "accountkey",
        "account_key",
        "signature"
    ];

    /// <summary>The never-log terms, in declaration order.</summary>
    /// <remarks>
    /// <b>A wrapper rather than the array, because <c>IReadOnlyList&lt;T&gt;</c>
    /// is a view and not a guarantee.</b> Returning <c>Terms</c> directly lets
    /// any caller write <c>(string[])SensitiveKeys.All</c> and rewrite the
    /// platform's never-log vocabulary at run time — which would make the
    /// list's whole argument false, since it is pinned by a test precisely so
    /// that a change to it has to be deliberate. <c>Array.AsReadOnly</c> wraps
    /// once at type initialisation and cannot be cast back; the private array
    /// stays for the loop below, which is the only hot path.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Terms);

    /// <summary>Whether a key names something the platform must not export.</summary>
    // A foreach rather than Terms.Any(t => key.Contains(t, ...)): the lambda
    // would capture `key`, so the closure allocates once per attribute
    // inspected — including on the no-match path the callers are written to
    // keep allocation-free. This runs on every attribute of every log record.
    public static bool Matches(string key)
    {
        foreach (string term in Terms)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a <em>value</em> carries a secret whatever its key is called.
    /// </summary>
    /// <remarks>
    /// <b>This is the half that survives a key nobody predicted.</b> The key
    /// list above can only catch a name someone thought of, and the failure is
    /// silent — no test can be written for the term that is missing. Two shapes
    /// are recognised because both are unmistakable and both are what this
    /// platform actually holds: a connection string, which every service builds
    /// from configuration and which carries <c>Password=</c> inline, and a JWT,
    /// which §11.3 puts on every authenticated request.
    /// <para>
    /// Deliberately not a general entropy test. A high-entropy string is an id
    /// as often as it is a credential, and redacting every id would empty the
    /// records an incident is triaged by — §13.1's whole argument.
    /// </para>
    /// </remarks>
    public static bool LooksLikeSecret(object? value)
    {
        if (value is not string text || text.Length == 0)
            return false;

        if (Assigns(text, "password") || Assigns(text, "pwd"))
            return true;

        // A JWT's header is base64url of a JSON object opening `{"`, which is
        // always the three characters below, and the compact serialisation has
        // exactly two dots. Anchored on the prefix so the dot count — the
        // expensive half — is reached by almost nothing.
        if (!text.StartsWith("eyJ", StringComparison.Ordinal))
            return false;

        int dots = 0;

        foreach (char c in text)
        {
            if (c == '.')
                dots++;
        }

        return dots == 2;
    }

    /// <summary>
    /// Whether <paramref name="text"/> assigns to <paramref name="key"/> — the
    /// key, then any whitespace, then <c>=</c>.
    /// </summary>
    /// <remarks>
    /// <b>The whitespace is the whole reason this is not a substring test.</b>
    /// An ADO.NET connection string is a list of `keyword=value` pairs and the
    /// parser tolerates spaces around the separator, so
    /// <c>Password = hunter2</c> is as valid as <c>Password=hunter2</c> and a
    /// check for the literal <c>password=</c> misses it — which is a value
    /// bypassing a guarantee written as "whatever its key is called".
    /// <para>
    /// Requiring the <c>=</c> is what keeps this from firing on prose: a
    /// message reading "the password was rejected" assigns nothing. Scanning
    /// rather than parsing, because the input is an arbitrary logged value and
    /// may not be a connection string at all — <c>SqlConnectionStringBuilder</c>
    /// would throw on most of what reaches here.
    /// </para>
    /// </remarks>
    private static bool Assigns(string text, string key)
    {
        int from = 0;

        while (from <= text.Length - key.Length)
        {
            int at = text.IndexOf(key, from, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                return false;

            int after = at + key.Length;

            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;

            if (after < text.Length && text[after] == '=')
                return true;

            from = at + 1;
        }

        return false;
    }
}
