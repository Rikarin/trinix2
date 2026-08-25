using System.Collections.Generic;

namespace Trinix.Settings;

/// <summary>
///     A set of writes that happen together or not at all.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 names the atomic multi-key write as one of the two reasons the store is a
///         database rather than a directory of files, so it is the <i>only</i> way to write:
///         there is no <c>store.Set(key, value)</c>, and a single-key change is a
///         transaction with one entry. That is deliberate. An API with both grows call sites
///         that set three related keys in three statements, and the fourth one to be added
///         is the one that fails after the first two landed.
///     </para>
///     <para>
///         ⚠ <b>Nothing is held between <see cref="Set" /> and <see cref="Commit" />.</b>
///         The entries are buffered in memory and the SQLite transaction is opened, applied
///         and closed inside <see cref="Commit" />. That is why this type is not
///         <see cref="IDisposable" /> and why abandoning one costs nothing: a transaction
///         object that held <c>BEGIN IMMEDIATE</c> while a caller decided what to write is a
///         write lock held across arbitrary user code, and doc 02 § Failure is explicit that
///         a service must not hold a lock across a call it does not control.
///     </para>
///     <para>
///         ⚠ Neither <see cref="Set" /> nor <see cref="Reset" /> consults the schemas this
///         process has registered. Both the "is this key declared" and the "is this the kind
///         it was declared with" checks live in the database — a foreign key and a trigger —
///         and fire inside the transaction. An in-memory check would be a second copy of the
///         rule that can drift from the first, and it would be wrong besides: another
///         process may have registered a schema this one has never seen.
///     </para>
/// </remarks>
public sealed class SettingsTransaction {
    readonly SettingsStore _store;
    readonly List<Entry> _entries = [];
    bool _committed;

    internal SettingsTransaction(SettingsStore store) => _store = store;

    internal IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Set a key in this store's writable layer.</summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write. Its kind must be the key's declared kind.</param>
    /// <returns>This transaction, so writes chain.</returns>
    /// <exception cref="ArgumentException"><paramref name="value" /> has no kind.</exception>
    /// <exception cref="InvalidOperationException">This transaction has already been committed.</exception>
    public SettingsTransaction Set(SettingsKey key, SettingValue value) {
        if (value.Kind == SettingKind.None) {
            throw new ArgumentException($"'{key}' cannot be set to nothing. Use Reset to remove it.", nameof(value));
        }

        return Add(new Entry(key, value, IsReset: false));
    }

    /// <summary>
    ///     Remove a key from this store's writable layer, so that it falls back to whatever
    ///     is underneath.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reset means "remove what this layer says", not "set to the schema default". On
    ///     an unmanaged machine those coincide; on a machine with an
    ///     <see cref="SettingsLayer.Administrator" /> database they do not, and resetting to
    ///     the compiled-in default rather than to the administrator's would be the wrong
    ///     answer and a very hard one to notice.
    /// </remarks>
    /// <param name="key">The key to remove.</param>
    /// <returns>This transaction, so writes chain.</returns>
    /// <exception cref="InvalidOperationException">This transaction has already been committed.</exception>
    public SettingsTransaction Reset(SettingsKey key) => Add(new Entry(key, default, IsReset: true));

    /// <summary>
    ///     Apply every entry in one SQLite transaction, then raise at most one change event.
    /// </summary>
    /// <remarks>
    ///     Either every entry lands or none does. If anything is refused — a key no schema
    ///     declares, a value of the wrong kind, a disk that is full — the transaction rolls
    ///     back and no event is raised, because nothing changed.
    /// </remarks>
    /// <exception cref="SettingsException">The write was refused.</exception>
    /// <exception cref="InvalidOperationException">This transaction has already been committed.</exception>
    public void Commit() {
        if (_committed) {
            throw new InvalidOperationException("This transaction has already been committed.");
        }

        _committed = true;
        _store.Apply(this);
    }

    SettingsTransaction Add(Entry entry) {
        if (_committed) {
            throw new InvalidOperationException("This transaction has already been committed.");
        }

        // Last write wins, and it wins in place rather than by appending: two Sets of one
        // key in one transaction is a caller changing its mind, not two writes, and the
        // change report should say the key moved once.
        for (var i = 0; i < _entries.Count; i++) {
            if (_entries[i].Key == entry.Key) {
                _entries[i] = entry;
                return this;
            }
        }

        _entries.Add(entry);
        return this;
    }

    /// <summary>One buffered write.</summary>
    internal readonly record struct Entry(SettingsKey Key, SettingValue Value, bool IsReset);
}
