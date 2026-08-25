# shellcheck shell=bash
# SQLite — one recipe, three consumers.
#
# docs/plan/02 § Settings storage wants a per-user database because layered,
# schema-typed preferences need atomic multi-key writes and a change
# notification that is not an inotify storm. docs/plan/03 wants notification
# history. docs/plan/06 wants the Beacon index, and it wants **FTS5**
# specifically — that document's argument is that the alternative is writing an
# inverted index, which is a month of work and a class of bugs for something
# that will still be worse. So FTS5 is not a nice-to-have here; it is the
# reason the recipe exists, and trinix_check proves it is in the library that
# ships rather than merely in the configure log.
#
# The consumers are C#, reaching this through Microsoft.Data.Sqlite over
# SQLitePCLRaw's *system* provider. That is what decides several flags below:
# a managed caller discovers a missing compile-time feature as an exception at
# runtime, in a service, on a machine with a serial console — which is the
# expensive way to find out.
#
# ---------------------------------------------------------------------------
# ⚠ This tarball's `configure` is not autoconf, whatever the name says
# ---------------------------------------------------------------------------
#
# sqlite-autoconf-*.tar.gz has been an **autosetup** package since 3.48. The
# script is TCL, and since the build container has no tclsh it compiles the
# bundled JimTCL (autosetup/jimsh0.c) with ${CC_FOR_BUILD:-cc} — deliberately
# not $CC, which is the cross compiler. That is correct and needs nothing from
# us, but it is worth knowing before reading a configure log that appears to
# build a C program with gcc in the middle of a Clang cross build.
#
# Two consequences that do matter:
#
#   * autosetup honours $CC/$CXX from the environment ahead of the
#     $host-prefixed names it would otherwise search for, so the driver's
#     exports are picked up and --host is only used to *declare* the cross.
#   * it knows it is cross-compiling (build != host) and therefore **skips**
#     several probes rather than running them wrongly. The readline probe is
#     the one that bites; see below.
#
# One diagnostic in the log is expected and can be ignored: `Failed to find
# <triple>-ld, falling back to ld which may be incorrect`. autosetup looks for
# a triple-prefixed ld because the driver deliberately does not export $LD —
# linking goes through the clang driver — and the answer is never used, since
# Makefile.in has no $(LD) in it at all. $AR is exported and is found.

RECIPE_SOURCE="sqlite"

# zlib is here for a reason that is not "libsqlite3 needs it".
#
# sqlite-config.tcl probes for zlib.h unconditionally and there is no flag to
# say no. If it finds one it defines SQLITE_HAVE_ZLIB=1 and puts -lz on *both*
# link lines — even though only shell.c ever reads that define, and the
# amalgamation never mentions it. The CLI gains a working `.archive`; the
# library gains a -lz it has no symbol for, which a linker that drops unused
# libraries will discard and one that does not will record as a DT_NEEDED.
#
# The wart is upstream's and small either way. What would not be small is the
# outcome being decided by whether zlib happened to be built before sqlite,
# which is exactly the "auto-detected optional dependency" failure
# base/recipes/README.md describes: the rootfs would differ between a warm
# cache and a clean build. Naming zlib here makes the ordering a fact rather
# than an accident.
#
# readline and ncurses are for the CLI's line editing, likewise answered
# explicitly rather than probed.
RECIPE_DEPENDS="glibc-runtime zlib ncurses readline"

trinix_build() {
    # -O2 and no -g. Modern sqlite already defaults to exactly this when it
    # detects a cross build, but "the upstream default happens to be right"
    # is not something a version bump preserves, and an unoptimised SQLite is
    # a search index that feels broken rather than one that fails.
    #
    # CPPFLAGS carries the one feature flag with no configure option in the
    # autoconf bundle. sqlite-config.tcl relocates -DSQLITE_ENABLE_*/-DSQLITE_OMIT_*
    # out of CPPFLAGS into OPT_FEATURE_FLAGS, so this reaches the library
    # rather than only the compile line — verified by reading the generated
    # Makefile, not assumed.
    #
    # ⚠ SQLITE_ENABLE_COLUMN_METADATA is what makes sqlite3_column_table_name()
    # and friends exist, and Microsoft.Data.Sqlite's GetSchemaTable() /
    # GetColumnSchema() are built on them. Without it those throw at runtime.
    # Every distribution that expects managed or ODBC callers enables it; we
    # have three managed callers.
    CFLAGS='-O2' \
    CPPFLAGS='-DSQLITE_ENABLE_COLUMN_METADATA' \
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        \
        `# ⚠ The trap this recipe most needs the comment for: sqlite sets NO` \
        `# SONAME by default. Upstream says so plainly in --help ("none, or` \
        `# not using this flag, sets no soname") because the canonical build` \
        `# has its own reasons to want that. For a shared library in a system` \
        `# image it is wrong: with no DT_SONAME the linker records the` \
        `# link-time *file name* in each dependant, so everything ends up` \
        `# needing "libsqlite3.so" — the development symlink — instead of the` \
        `# versioned object. "legacy" is libsqlite3.so.0, which is what every` \
        `# other distribution ships and what a prebuilt managed dependency` \
        `# would go looking for.` \
        --soname=legacy \
        \
        `# The whole point of the recipe. FTS5 pulls in the math functions,` \
        `# which is why --disable-math is not somewhere below.` \
        --fts5 \
        \
        `# The CLI links libsqlite3.so.0 instead of embedding its own copy of` \
        `# sqlite3.c. Two reasons. A second, statically linked SQLite in an` \
        `# immutable image is a megabyte of duplicate code — and, worse, a` \
        `# second *configuration*: a static shell would answer questions about` \
        `# a library that is not the one the services load. With this, what` \
        `# you inspect on the console is the object that ships.` \
        --disable-static-shell \
        \
        `# ...which makes this necessary rather than optional. shell.c is` \
        `# always compiled with -DSQLITE_ENABLE_DBPAGE_VTAB (it is in` \
        `# upstream's fixed OPT_SHELL list, not something we chose), and that` \
        `# switches on the .recover command — whose implementation reads` \
        `# "SELECT data FROM sqlite_dbpage(...)". The vtab it queries is a` \
        `# *library* feature. Leave it off and .recover links fine and then` \
        `# fails on a corrupt database, which is the only time anyone runs it.` \
        --dbpage \
        \
        `# ⚠ readline, and the reason it is spelled out rather than left on` \
        `# "auto". sqlite-check-line-editing skips the readline.h search` \
        `# entirely when cross-compiling — it warns and moves on — so "auto"` \
        `# does not mean "look in the sysroot", it means "no line editing,` \
        `# silently". Naming the flags takes the explicit branch, which does` \
        `# no probing at all. Worth having: the sqlite3 CLI is how a settings` \
        `# database or a Beacon index gets looked at on a machine that has a` \
        `# serial console and nothing else, and readline and ncursesw are` \
        `# already in the image for bash.` \
        --with-readline-cflags="-I$SYSROOT/usr/include" \
        --with-readline-ldflags='-lreadline -lncursesw' \
        \
        `# No libsqlite3.a: nothing in the base set links SQLite statically,` \
        `# and the archive is build-container weight the image would carry.` \
        --disable-static \
        \
        `# The default rpath is $prefix/lib, i.e. an RUNPATH of /usr/lib on an` \
        `# object installed in /usr/lib. Noise in a read-only image.` \
        --disable-rpath

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -rf "$DESTDIR/usr/share/man"
}

# ---------------------------------------------------------------------------
# Deliberately *not* set, because each is a trap for a shared system library
# ---------------------------------------------------------------------------
#
# sqlite.org publishes a "recommended compile-time options" list, and most of
# it is advice for an application that embeds SQLite and owns every caller.
# This is a shared object with callers not yet written:
#
#   SQLITE_OMIT_DECLTYPE      Microsoft.Data.Sqlite maps column types with
#                             sqlite3_column_decltype(). Omitting it breaks
#                             every consumer we have.
#   SQLITE_OMIT_DEPRECATED    same shape of problem, one release later.
#   SQLITE_OMIT_AUTOINIT      moves sqlite3_initialize() into the caller's
#                             contract. A caller that predates the change
#                             crashes rather than fails.
#   SQLITE_DQS=0              a genuine improvement and a genuine SQL dialect
#                             change. It belongs to whoever owns the schema,
#                             not to the recipe that builds the engine.
#
# Extension loading is left enabled for a narrower reason: sqlite3_load_extension()
# is already refused at runtime unless a caller explicitly opts in with
# sqlite3_db_config(SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION), so omitting it at
# compile time buys almost no hardening while risking a managed binding that
# resolves the symbol eagerly.

trinix_check() {
    local lib="$DESTDIR/usr/lib/libsqlite3.so.0"
    local cli="$DESTDIR/usr/bin/sqlite3"

    # The pin and the source must describe the same release.
    #
    # sqlite's tarball name carries its version as one number — 3.53.4 is
    # 3_53_04_00 — so base/sources.json pins that form, which is unreadable
    # and easy to leave stale next to a URL that also has a hand-edited year
    # in it. The unpacked tree states the dotted version outright, so the two
    # can simply be compared.
    local pinned dotted maj min pat
    pinned="$(trinix-fetch --version sqlite)"
    dotted="$(cat "$SRCDIR/VERSION")"
    IFS=. read -r maj min pat <<< "$dotted"
    [ "$pinned" = "$(printf '%d%02d%02d00' "$maj" "$min" "$pat")" ] \
        || { echo "sqlite: sources.json pins $pinned but the tarball is $dotted — the URL and the version disagree" >&2; return 1; }

    [ -e "$lib" ] \
        || { echo 'sqlite: libsqlite3.so.0 missing — did --soname=legacy stop working?' >&2; return 1; }
    [ ! -e "$DESTDIR/usr/lib/libsqlite3.a" ] \
        || { echo 'sqlite: a static archive was installed — --disable-static did not take' >&2; return 1; }

    # The soname itself, not just a file with that name. install-dll-unix-generic
    # creates libsqlite3.so.0 as a symlink whether or not DT_SONAME was set, so
    # the file existing proves nothing about what dependants will record.
    "$READELF" --dynamic "$lib" | grep -q 'SONAME.*libsqlite3\.so\.0' \
        || { echo 'sqlite: libsqlite3.so.0 carries no SONAME — every dependant would record "libsqlite3.so" instead' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$lib" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "sqlite: libsqlite3.so.0 is '$machine', wrong for $TRINIX_ARCH" >&2; return 1 ;;
    esac

    # --disable-static-shell took effect, so what the next check exercises
    # through the CLI really is the shipped library.
    "$READELF" --dynamic "$cli" | grep -q 'libsqlite3\.so\.0' \
        || { echo 'sqlite: the CLI does not link libsqlite3.so.0 — it embedded its own copy' >&2; return 1; }

    # FTS5, statically. fts5_source_id() is registered only inside the
    # SQLITE_ENABLE_FTS5 block of the amalgamation, so its name appearing in
    # the object is a sufficient answer and needs no emulator. It runs first
    # precisely so that a failure of the *next* check names the harness rather
    # than the feature.
    grep -qa 'fts5_source_id' "$lib" \
        || { echo 'sqlite: no FTS5 in libsqlite3.so.0 — docs/plan/06 cannot build an index on this' >&2; return 1; }

    # ...and then FTS5 for real, because "the symbol is present" and "MATCH
    # returns the right row" are different claims and only the second one is
    # what Beacon needs.
    #
    # The library is not in $SYSROOT yet — install_recipe runs after this — so
    # the loader is invoked directly with an explicit --library-path rather
    # than being left to find it. That also sidesteps ld.so.cache, which in a
    # cross build belongs to the container.
    local loader
    case "$TRINIX_ARCH" in
        arm64)  loader='ld-linux-aarch64.so.1' ;;
        x86_64) loader='ld-linux-x86-64.so.2'  ;;
        *) echo "sqlite: unknown architecture '$TRINIX_ARCH'" >&2; return 1 ;;
    esac

    local hits
    hits="$("$QEMU" -L "$SYSROOT" "$SYSROOT/usr/lib/$loader" \
                --library-path "$DESTDIR/usr/lib:$SYSROOT/usr/lib" \
                "$cli" -batch :memory: \
                "CREATE VIRTUAL TABLE t USING fts5(body);
                 INSERT INTO t VALUES('the quick brown fox');
                 SELECT count(*) FROM t WHERE t MATCH 'brown';" 2>&1)" \
        || { echo "sqlite: the CLI did not run under qemu-user: $hits" >&2; return 1; }

    [ "$hits" = '1' ] \
        || { echo "sqlite: an FTS5 MATCH returned '$hits' rather than 1" >&2; return 1; }
}
