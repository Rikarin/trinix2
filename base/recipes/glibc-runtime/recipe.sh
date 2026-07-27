# shellcheck shell=bash
# glibc, as shipped rather than as built.
#
# glibc is built in Phase 1 because the cross toolchain cannot exist without it,
# so there is nothing to compile here. What this recipe does is decide which
# parts of that install belong on the running system: the dynamic loader and the
# shared libraries, but none of the headers, static archives or build-time
# machinery that only matter inside the build container.
#
# Everything else in the base depends on this, so it sorts first.

RECIPE_SOURCE=""          # synthetic: assembled from the sysroot
RECIPE_DEPENDS=""

trinix_build() {
    install -d "$DESTDIR/usr/lib" "$DESTDIR/usr/bin" "$DESTDIR/etc"

    # The loader and the shared libraries. -P keeps the soname symlinks as
    # symlinks instead of duplicating multi-megabyte files.
    for lib in "$SYSROOT"/usr/lib/ld-linux-*.so.* \
               "$SYSROOT"/usr/lib/lib*.so.[0-9]*; do
        [ -e "$lib" ] || continue
        case "${lib##*/}" in
            # Shipped by llvm-runtime instead — see that recipe for why the
            # C++ stack is deliberately kept separate from libc.
            libc++*|libunwind*|libstdc++*|libgcc_s*|libatomic*|libgomp*) continue ;;
        esac
        cp -P "$lib" "$DESTDIR/usr/lib/"
    done

    # Resolve the soname symlinks that live alongside them (libm.so.6 -> ...).
    for link in "$SYSROOT"/usr/lib/lib*.so.[0-9]; do
        [ -L "$link" ] && cp -P "$link" "$DESTDIR/usr/lib/"
    done

    # A handful of glibc's own programs are genuinely useful on the system.
    for prog in ldd getent locale iconv; do
        [ -e "$SYSROOT/usr/bin/$prog" ] && cp -P "$SYSROOT/usr/bin/$prog" "$DESTDIR/usr/bin/"
    done

    # ld.so.conf exists so the loader does not warn; the merged-/usr layout
    # means there is nothing extra to add to it.
    printf '# Trinix uses a merged /usr; no additional library paths.\n' \
        > "$DESTDIR/etc/ld.so.conf"

    # Minimal NSS configuration. Without this, getpwnam() fails and systemd
    # cannot resolve the users its units run as.
    cat > "$DESTDIR/etc/nsswitch.conf" <<'EOF'
passwd:     files
group:      files
shadow:     files
hosts:      files dns
networks:   files
protocols:  files
services:   files
ethers:     files
rpc:        files
EOF
}

trinix_check() {
    # The loader is the one file whose absence turns every dynamic binary in
    # the image into "No such file or directory", which is a famously
    # misleading way to discover it is missing.
    ls "$DESTDIR"/usr/lib/ld-linux-*.so.* >/dev/null \
        || { echo "glibc-runtime: no dynamic loader in the rootfs" >&2; return 1; }
    [ -e "$DESTDIR/usr/lib/libc.so.6" ] \
        || { echo "glibc-runtime: libc.so.6 missing" >&2; return 1; }
}
