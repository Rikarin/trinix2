# shellcheck shell=bash
# iproute2 — `ip`, and the netlink tooling underneath it.
#
# systemd-networkd is deliberately not built (see the systemd recipe), so
# something has to be able to bring an interface up and add a route. Until the
# Phase 8 networking frontend exists, that something is `ip`.

RECIPE_SOURCE="iproute2"
RECIPE_DEPENDS="glibc-runtime libcap"

trinix_build() {
    # No out-of-tree build support, and the source tree is shared between both
    # architectures — so build from a copy, as zstd does.
    cp -a "$SRCDIR/." "$BUILDDIR/"
    cd "$BUILDDIR"

    # libbpf would pull in libelf and a BPF loader; nothing in the base image
    # attaches BPF programs, and `ip` degrades cleanly without it.
    ./configure --libbpf_force off

    # Two things this Makefile does that a cross build has to override:
    #
    #   CC := gcc            a simply-expanded assignment, so it beats the
    #                        environment and must be given on the command line
    #   KERNEL_INCLUDE=/usr/include
    #                        which on a cross build means the *host's* headers
    #
    # HOSTCC stays gcc: iproute2 builds a couple of generators that run here.
    local args=(
        CC="$CC"
        HOSTCC=gcc
        KERNEL_INCLUDE="$SYSROOT/usr/include"
        PREFIX=/usr
        SBINDIR=/usr/bin
        CONF_ETC_DIR=/etc/iproute2
        NETNS_RUN_DIR=/run/netns
    )

    make -j"$JOBS" "${args[@]}"
    make "${args[@]}" DESTDIR="$DESTDIR" install

    # Manual pages and the bash completions have no reader in the image.
    rm -rf "$DESTDIR/usr/share/man" "$DESTDIR/usr/share/bash-completion"
}

trinix_check() {
    [ -x "$DESTDIR/usr/bin/ip" ] || { echo 'iproute2: ip missing' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$DESTDIR/usr/bin/ip" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "iproute2: ip is '$machine', wrong for $TRINIX_ARCH — HOSTCC leaked into the target build" >&2; return 1 ;;
    esac
}
