using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Trinix.Settings;

/// <summary>
///     The settings store of doc 02 § Settings storage: typed, schema-declared, layered,
///     transactional, and eventful.
/// </summary>
/// <remarks>
///     <para>
///         One SQLite database per user, plus up to three machine-wide databases attached to
///         the same connection. Doc 02 gives the reason for the database in one sentence —
///         atomic multi-key writes and a watch that is not an inotify storm are both
///         required and neither is possible over a directory of files — and both fall out of
///         a transaction here rather than being built.
///     </para>
///     <para>
///         ⚠ <b>Layer resolution is one SQL statement, not four lookups in C#.</b> The
///         layers are <c>ATTACH</c>ed, so a read is a <c>UNION ALL</c> over the attached
///         databases ordered by precedence, evaluated against one consistent snapshot. Four
///         separate connections would give four snapshots, and a read taken across a
///         concurrent write to a higher layer could resolve to a value that never existed.
///     </para>
///     <para>
///         ⚠ <b>The store writes exactly one layer</b> — the one it was opened for, which is
///         also the one SQLite calls <c>main</c>. A session opens
///         <see cref="SettingsLayer.User" />; administrative tooling opens
///         <see cref="SettingsLayer.Administrator" /> or
///         <see cref="SettingsLayer.ManagedProfile" /> deliberately and separately. There is
///         no API that takes a layer as an argument to a write, because the day there is,
///         something will pass <c>ManagedProfile</c> from a UI event handler.
///     </para>
///     <para>
///         ⚠ The invariants live in the schema, not in this file. "A value may exist only for
///         a declared key" is a foreign key; "a value has the kind its key was declared with"
///         is a trigger. Both were an <c>if</c> in the first draft, and an <c>if</c> here is a
///         rule that holds for callers that come through this class — which is not the same
///         set as the callers that come through <c>sqlite3</c> on a serial console.
///     </para>
///     <para>
///         Change notification is in-process: <see cref="Changed" /> fires once per commit
///         that moved something. Doc 02 wants the event to cross processes too, and the
///         mechanism for that is the D-Bus signal on the settings service, which fronts this
///         type and is not built here. ⚠ Until it is, two stores over the same file do not
///         see each other's writes as events; they see them on the next read, because a read
///         goes to the database rather than to a cache.
///     </para>
/// </remarks>
public sealed class SettingsStore : IDisposable {
    // Deliberately typeless value columns. A column declared TEXT has TEXT affinity, and
    // SQLite would then convert an integer 1 into the string '1' on the way in — which
    // reads back as a kind mismatch and resolves to the default. The kind column carries
    // the type; the value column must carry the value it was given.
    const string Ddl = """
        CREATE TABLE IF NOT EXISTS main.trinix_schema (
            schema_id     TEXT NOT NULL,
            name          TEXT NOT NULL,
            kind          TEXT NOT NULL,
            default_value NOT NULL,
            summary       TEXT NOT NULL,
            PRIMARY KEY (schema_id, name)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS main.trinix_value (
            schema_id TEXT NOT NULL,
            name      TEXT NOT NULL,
            kind      TEXT NOT NULL,
            value     NOT NULL,
            PRIMARY KEY (schema_id, name),
            FOREIGN KEY (schema_id, name)
                REFERENCES trinix_schema (schema_id, name) ON DELETE CASCADE
        ) WITHOUT ROWID;

        CREATE TRIGGER IF NOT EXISTS main.trinix_value_kind_on_insert
        BEFORE INSERT ON trinix_value FOR EACH ROW
        WHEN NEW.kind <> (SELECT kind FROM trinix_schema WHERE schema_id = NEW.schema_id AND name = NEW.name)
        BEGIN
            SELECT RAISE(ABORT, 'the value is not the kind this key was declared with');
        END;

        CREATE TRIGGER IF NOT EXISTS main.trinix_value_kind_on_update
        BEFORE UPDATE ON trinix_value FOR EACH ROW
        WHEN NEW.kind <> (SELECT kind FROM trinix_schema WHERE schema_id = NEW.schema_id AND name = NEW.name)
        BEGIN
            SELECT RAISE(ABORT, 'the value is not the kind this key was declared with');
        END;
        """;

    // SQLite's own name for the writable database. Every other layer is attached under one
    // of the names below; none of them is ever built from caller input, which is why the
    // resolve statement can be assembled as text.
    const string MainDatabase = "main";

    readonly object _gate = new();
    readonly Dictionary<string, SettingsSchema> _schemas = new(StringComparer.Ordinal);
    readonly SqliteConnection _connection;
    readonly string _resolveSql;
    bool _disposed;

    SettingsStore(SqliteConnection connection, SettingsLayer writableLayer, string resolveSql) {
        _connection = connection;
        WritableLayer = writableLayer;
        _resolveSql = resolveSql;
    }

    /// <summary>Raised once per commit that changed at least one resolved value.</summary>
    /// <remarks>
    ///     ⚠ Raised outside the store's lock, on the thread that called
    ///     <see cref="SettingsTransaction.Commit" />. A handler may read settings; a handler
    ///     that writes them re-enters, which works and is still a bad idea, because the
    ///     nested commit's event is delivered before the outer one's.
    /// </remarks>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>The one layer this store writes.</summary>
    public SettingsLayer WritableLayer { get; }

    /// <summary>Every schema registered with this store, for doc 08's search index and for tooling.</summary>
    public IReadOnlyCollection<SettingsSchema> Schemas {
        get {
            lock (_gate) {
                return [.. _schemas.Values];
            }
        }
    }

    /// <summary>Open the user's store, creating it if this is the first run.</summary>
    /// <param name="layout">Where each layer lives.</param>
    /// <exception cref="SettingsProviderException">The loaded SQLite is not one this store can use.</exception>
    public static SettingsStore Open(SettingsLayout layout) => Open(layout, SettingsLayer.User);

    /// <summary>
    ///     Open the store, writing one named layer.
    /// </summary>
    /// <param name="layout">Where each layer lives.</param>
    /// <param name="writableLayer">
    ///     The layer to write. <see cref="SettingsLayer.User" /> for a session; the others
    ///     only for the tooling that authors them.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <exception cref="ArgumentException">The layout has no path for <paramref name="writableLayer" />.</exception>
    /// <exception cref="SettingsProviderException">The loaded SQLite is not one this store can use.</exception>
    public static SettingsStore Open(SettingsLayout layout, SettingsLayer writableLayer) {
        ArgumentNullException.ThrowIfNull(layout);

        var mainPath = layout.PathFor(writableLayer)
            ?? throw new ArgumentException(
                $"This layout has no path for the {writableLayer} layer, so there is nothing to write.",
                nameof(layout)
            );

        // ⚠ Batteries_V2 comes from whichever SQLitePCLRaw bundle the .csproj selected for
        // this RID — e_sqlite3 off-target, the system libsqlite3.so.0 on it. The call is the
        // same either way and is idempotent; what differs is the library it initialises, and
        // SqliteRequirements.Verify below is what says which one turned up.
        SQLitePCL.Batteries_V2.Init();

        if (Path.GetDirectoryName(Path.GetFullPath(mainPath)) is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = mainPath,
            // Asked for on the connection string *and* set as a pragma below. The connection
            // string is how Microsoft.Data.Sqlite is told; the pragma is what SQLite is told;
            // and SqliteRequirements.Verify reads back what actually took, because the whole
            // point is not to trust a default that differs between the two builds.
            ForeignKeys = true,

            // ⚠ No pooling, and this is not a performance opinion. Microsoft.Data.Sqlite
            // pools by connection string and does *not* detach attached databases when a
            // connection goes back to the pool, so the second store opened over one layout
            // in a process gets a connection that still has the first store's ATTACHes on
            // it — which fails outright with "database … is already in use", and would be
            // far worse if it did not: a pooled connection carrying an ATTACH to a layer
            // file that has since been replaced would resolve settings from a database
            // nobody can see. A store holds one connection for the life of the process, so
            // there is nothing here for a pool to amortise anyway.
            Pooling = false,
        }.ToString());

        try {
            connection.Open();
            Configure(connection, writableLayer);
            SqliteRequirements.Verify(connection);
            Execute(connection, Ddl);

            var layers = Attach(connection, layout, writableLayer);
            return new SettingsStore(connection, writableLayer, ResolveStatement(layers));
        } catch {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Declare a schema to this store, idempotently.
    /// </summary>
    /// <remarks>
    ///     Registering does two things: it makes the keys readable through
    ///     <see cref="Get" />, and it writes the declaration into the writable database so
    ///     that the foreign key and the kind trigger have something to check against — and
    ///     so that <c>sqlite3</c> on a console can say what a key is for.
    ///     ⚠ Registering the same identifier twice with a different declaration throws.
    ///     Two components disagreeing about what a schema contains is not something to
    ///     resolve by last-writer-wins: one of them will read a default it did not expect.
    /// </remarks>
    /// <param name="schema">The schema to declare.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema" /> is null.</exception>
    /// <exception cref="SettingsSchemaException">A different schema is already registered under this identifier.</exception>
    public void Register(SettingsSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);

        lock (_gate) {
            ThrowIfDisposed();

            if (_schemas.TryGetValue(schema.Id, out var existing)) {
                if (existing.Declares(schema)) {
                    return;
                }

                throw new SettingsSchemaException(
                    $"'{schema.Id}' is already registered with a different declaration. Two components "
                    + "that disagree about a schema will disagree about its defaults, and one of them "
                    + "will read a value it never expected."
                );
            }

            Execute(_connection, "BEGIN IMMEDIATE");
            try {
                foreach (var setting in schema.Settings) {
                    using var command = _connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO main.trinix_schema (schema_id, name, kind, default_value, summary)
                        VALUES ($schema, $name, $kind, $default, $summary)
                        ON CONFLICT (schema_id, name) DO UPDATE SET
                            kind = excluded.kind,
                            default_value = excluded.default_value,
                            summary = excluded.summary
                        """;
                    command.Parameters.AddWithValue("$schema", schema.Id);
                    command.Parameters.AddWithValue("$name", setting.Name);
                    command.Parameters.AddWithValue("$kind", KindName(setting.Kind));
                    command.Parameters.AddWithValue("$default", Raw(setting.Default));
                    command.Parameters.AddWithValue("$summary", setting.Summary);
                    command.ExecuteNonQuery();
                }

                Execute(_connection, "COMMIT");
            } catch {
                Rollback(_connection);
                throw;
            }

            _schemas[schema.Id] = schema;
        }
    }

    /// <summary>The value a key resolves to.</summary>
    /// <param name="key">The key to read.</param>
    /// <exception cref="SettingsSchemaException">No registered schema declares this key.</exception>
    public SettingValue Get(SettingsKey key) => Resolve(key).Value;

    /// <summary>The value a key resolves to, and which layer decided it.</summary>
    /// <param name="key">The key to read.</param>
    /// <exception cref="SettingsSchemaException">No registered schema declares this key.</exception>
    public SettingsResolution Resolve(SettingsKey key) {
        lock (_gate) {
            ThrowIfDisposed();
            return TryResolve(key) ?? throw new SettingsSchemaException(key);
        }
    }

    /// <summary>Begin a set of writes that will happen together or not at all.</summary>
    public SettingsTransaction BeginWrite() {
        lock (_gate) {
            ThrowIfDisposed();
        }

        return new SettingsTransaction(this);
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    // --- the write path -----------------------------------------------------

    internal void Apply(SettingsTransaction transaction) {
        if (transaction.Entries.Count == 0) {
            return;
        }

        List<SettingsResolution>? changes = null;

        lock (_gate) {
            ThrowIfDisposed();

            // ⚠ Resolved *before*, not stored-before. What a subscriber cares about is
            // whether the answer moved, and a write to the user layer under a managed
            // profile changes the database without changing any answer.
            var before = new Dictionary<SettingsKey, SettingValue>(transaction.Entries.Count);
            foreach (var entry in transaction.Entries) {
                if (TryResolve(entry.Key) is { } resolution) {
                    before[entry.Key] = resolution.Value;
                }
            }

            Execute(_connection, "BEGIN IMMEDIATE");
            var current = default(SettingsKey);
            try {
                foreach (var entry in transaction.Entries) {
                    current = entry.Key;
                    if (entry.IsReset) {
                        Delete(entry.Key);
                    } else {
                        Upsert(entry.Key, entry.Value);
                    }
                }

                Execute(_connection, "COMMIT");
            } catch (SqliteException failure) when (failure.SqliteErrorCode == SqliteConstraint) {
                Rollback(_connection);

                // The two constraints in the DDL are the two halves of doc 02's schema rule,
                // so a constraint failure is always one of them and is worth naming. Every
                // other SQLite error is left alone: SQLITE_BUSY, SQLITE_READONLY and
                // SQLITE_FULL are three different mornings and the result code is the only
                // part of the message that says which.
                throw new SettingsSchemaException(
                    $"'{current}' was refused: {failure.Message}. Either no schema declares it — doc 02: "
                    + "a key with no schema is a bug — or the value is not the kind it was declared with. "
                    + "Nothing in this transaction was written."
                );
            } catch {
                Rollback(_connection);
                throw;
            }

            foreach (var entry in transaction.Entries) {
                if (TryResolve(entry.Key) is not { } resolution) {
                    // Declared in the database but not registered in this process, so this
                    // store cannot type the value and has nothing meaningful to report.
                    continue;
                }

                if (before.TryGetValue(entry.Key, out var was) && was == resolution.Value) {
                    continue;
                }

                (changes ??= []).Add(resolution);
            }
        }

        // Outside the lock: a subscriber is arbitrary code, and doc 02 § Failure is explicit
        // that a service must not hold a lock across a call it does not control.
        if (changes is { Count: > 0 }) {
            Changed?.Invoke(this, new SettingsChangedEventArgs(changes));
        }
    }

    void Upsert(SettingsKey key, SettingValue value) {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO main.trinix_value (schema_id, name, kind, value)
            VALUES ($schema, $name, $kind, $value)
            ON CONFLICT (schema_id, name) DO UPDATE SET kind = excluded.kind, value = excluded.value
            """;
        command.Parameters.AddWithValue("$schema", key.Schema);
        command.Parameters.AddWithValue("$name", key.Name);
        command.Parameters.AddWithValue("$kind", KindName(value.Kind));
        command.Parameters.AddWithValue("$value", Raw(value));
        command.ExecuteNonQuery();
    }

    void Delete(SettingsKey key) {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM main.trinix_value WHERE schema_id = $schema AND name = $name";
        command.Parameters.AddWithValue("$schema", key.Schema);
        command.Parameters.AddWithValue("$name", key.Name);
        command.ExecuteNonQuery();
    }

    // --- the read path ------------------------------------------------------

    SettingsResolution? TryResolve(SettingsKey key) {
        if (!_schemas.TryGetValue(key.Schema, out var schema) || schema.Find(key.Name) is not { } descriptor) {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = _resolveSql;
        command.Parameters.AddWithValue("$schema", key.Schema);
        command.Parameters.AddWithValue("$name", key.Name);

        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            // ⚠ A layer whose stored kind disagrees with the declaration is skipped, not
            // thrown on, and the next layer down answers. The alternative is a desktop that
            // will not start because an administrator's database is one version stale, and a
            // preference is not worth that.
            if (ParseKind(reader.GetString(0)) != descriptor.Kind) {
                continue;
            }

            if (TryDecode(reader, descriptor.Kind) is not { } value) {
                continue;
            }

            return new SettingsResolution(key, value, (SettingsLayer)reader.GetInt32(2));
        }

        return new SettingsResolution(key, descriptor.Default, null);
    }

    static SettingValue? TryDecode(SqliteDataReader reader, SettingKind kind) {
        try {
            return kind switch {
                SettingKind.Bool => SettingValue.Bool(reader.GetInt64(1) != 0),
                SettingKind.Integer => SettingValue.Integer(reader.GetInt64(1)),
                SettingKind.Real => SettingValue.Real(reader.GetDouble(1)),
                SettingKind.Text => SettingValue.Text(reader.GetString(1)),
                SettingKind.Enum => SettingValue.EnumMember(reader.GetString(1)),
                _ => null,
            };
        } catch (InvalidCastException) {
            // The row says Integer and the cell holds text — a hand-edited database, or one
            // written by something that is not this library. Same answer as a kind mismatch:
            // fall through to the layer below.
            return null;
        }
    }

    // --- opening ------------------------------------------------------------

    static void Configure(SqliteConnection connection, SettingsLayer writableLayer) {
        // ⚠ Every one of these is set rather than assumed, because the two SQLite builds
        // Trinix's code meets do not agree about the defaults. See the .csproj header.
        Execute(connection, "PRAGMA foreign_keys = ON");

        // A settings write is small and rare and a lost one is a user's edit vanishing, so
        // durability beats throughput here by a wide margin.
        Execute(connection, "PRAGMA synchronous = FULL");

        // The store is a service and the sqlite3 CLI may be reading the same file from a
        // console. Five seconds of waiting beats SQLITE_BUSY reaching a Settings pane.
        Execute(connection, "PRAGMA busy_timeout = 5000");

        if (writableLayer == SettingsLayer.User) {
            // ⚠ WAL for the user's database and *only* the user's. Journal mode is persisted
            // in the file header, and a WAL database needs to create a -shm alongside itself
            // even to be read — which is impossible for a system layer living in an immutable
            // image. An administrator database authored in WAL mode would open fine on the
            // machine that authored it and fail to open on every machine it was installed on.
            Execute(connection, "PRAGMA main.journal_mode = WAL");
        }
    }

    static List<(SettingsLayer Layer, string Database)> Attach(
        SqliteConnection connection,
        SettingsLayout layout,
        SettingsLayer writableLayer
    ) {
        var layers = new List<(SettingsLayer, string)> { (writableLayer, MainDatabase) };

        foreach (var layer in (ReadOnlySpan<SettingsLayer>)[
                     SettingsLayer.SystemDefault,
                     SettingsLayer.Administrator,
                     SettingsLayer.User,
                     SettingsLayer.ManagedProfile,
                 ]) {
            if (layer == writableLayer) {
                continue;
            }

            // ⚠ File.Exists first: ATTACH creates the file it is pointed at, so attaching
            // unconditionally would have a session quietly create a root-owned-looking
            // /etc/trinix/settings/managed.db the first time it started — an empty policy
            // database that then exists forever.
            var path = layout.PathFor(layer);
            if (path is null || !File.Exists(path)) {
                continue;
            }

            var database = DatabaseName(layer);
            using (var attach = connection.CreateCommand()) {
                attach.CommandText = $"ATTACH DATABASE $path AS {database}";
                attach.Parameters.AddWithValue("$path", path);
                attach.ExecuteNonQuery();
            }

            // A file that is not a Trinix settings database — an empty file, someone else's
            // SQLite — is detached again rather than left to make every read fail with "no
            // such table" from a layer nobody asked about.
            using (var probe = connection.CreateCommand()) {
                probe.CommandText =
                    $"SELECT count(*) FROM {database}.sqlite_master WHERE type = 'table' AND name = 'trinix_value'";
                if (Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) {
                    Execute(connection, $"DETACH DATABASE {database}");
                    continue;
                }
            }

            layers.Add((layer, database));
        }

        return layers;
    }

    /// <summary>
    ///     The one statement that implements doc 02's layering.
    /// </summary>
    /// <remarks>
    ///     Assembled as text because the number of attached layers is not known until open,
    ///     and SQL has no way to parameterise a schema name. ⚠ Nothing interpolated here
    ///     comes from a caller: the database names are the four constants in
    ///     <see cref="DatabaseName" /> and the ranks are the enum's values. The two things a
    ///     caller does supply — the schema identifier and the key name — are bound
    ///     parameters, and stay bound parameters.
    /// </remarks>
    static string ResolveStatement(List<(SettingsLayer Layer, string Database)> layers) {
        var sql = new StringBuilder();
        foreach (var (layer, database) in layers) {
            if (sql.Length > 0) {
                sql.Append(" UNION ALL ");
            }

            sql.Append("SELECT kind, value, ")
                .Append((int)layer)
                .Append(" AS layer FROM ")
                .Append(database)
                .Append(".trinix_value WHERE schema_id = $schema AND name = $name");
        }

        // Highest layer first, so the reader takes the first row it can decode and stops.
        return "SELECT kind, value, layer FROM (" + sql + ") ORDER BY layer DESC";
    }

    static string DatabaseName(SettingsLayer layer) => layer switch {
        SettingsLayer.SystemDefault => "trinix_system",
        SettingsLayer.Administrator => "trinix_administrator",
        SettingsLayer.User => "trinix_user",
        SettingsLayer.ManagedProfile => "trinix_managed",
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Not a settings layer."),
    };

    // --- plumbing -----------------------------------------------------------

    const int SqliteConstraint = 19;

    static void Execute(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static void Rollback(SqliteConnection connection) {
        try {
            Execute(connection, "ROLLBACK");
        } catch (SqliteException) {
            // The transaction is already gone — SQLite rolls back by itself on some errors.
            // Throwing here would replace the failure the caller needs to see with this one.
        }
    }

    static object Raw(SettingValue value) => value.Kind switch {
        SettingKind.Bool => value.AsBool() ? 1L : 0L,
        SettingKind.Integer => value.AsInteger(),
        SettingKind.Real => value.AsReal(),
        SettingKind.Text => value.AsText(),
        SettingKind.Enum => value.AsEnumMember(),
        _ => throw new ArgumentException("A value with no kind cannot be stored.", nameof(value)),
    };

    // Spelled out rather than Enum.ToString/Enum.Parse: these strings are a *storage*
    // format, and a rename of an enum member must be a compile error here rather than a
    // silent change to what is in every user's database.
    static string KindName(SettingKind kind) => kind switch {
        SettingKind.Bool => "Bool",
        SettingKind.Integer => "Integer",
        SettingKind.Real => "Real",
        SettingKind.Text => "Text",
        SettingKind.Enum => "Enum",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a setting kind."),
    };

    static SettingKind ParseKind(string kind) => kind switch {
        "Bool" => SettingKind.Bool,
        "Integer" => SettingKind.Integer,
        "Real" => SettingKind.Real,
        "Text" => SettingKind.Text,
        "Enum" => SettingKind.Enum,
        _ => SettingKind.None,
    };

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
