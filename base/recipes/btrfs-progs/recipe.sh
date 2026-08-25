# shellcheck shell=bash
# btrfs-progs — mkfs.btrfs, btrfs(8) and libbtrfsutil, for /data.
#
# The A/B root slots stay ext4 (read-only images built on the Mac, replaced
# wholesale, never snapshotted), so this does not displace e2fsprogs: it sits
# next to it. What it is for is the *writable* partition.
#
# docs/plan/10 § Rewind is the whole argument. Time Machine on Linux is either
# a filesystem that can snapshot or a tool that copies files, and a tool that
# copies files cannot do the two things the document actually asks for —
# "browse a backup as if it were a normal filesystem" (a snapshot *is* a
# directory) and an offsite backup that is an incremental stream rather than a
# file-by-file walk (`btrfs send`/`receive`). ZFS is out on licence and
# out-of-tree grounds; LVM snapshots are block-level and cannot send
# incrementally. That leaves Btrfs, and Btrfs's userspace is this package.
#
# Three groups of consumers, all of them still to be written:
#
#   * image assembly and the installer — mkfs.btrfs with the subvolume layout
#     (see image/scripts/build-image.sh, which does the same thing with the
#     build container's copy);
#   * Rewind — subvolume create/snapshot/delete, `btrfs send | receive`,
#     `btrfs filesystem usage` for the space budget;
#   * Recovery (doc 10 § Recovery) — "unlock and repair /data" means
#     `btrfs check --repair` and `btrfs rescue`, on a slot whose whole job is
#     to work when the system does not.
#
# ⚠ A note on which mkfs.btrfs makes the shipped image: not this one. The disk
# image is assembled in the Debian build container by *its* btrfs-progs, the
# same way mke2fs builds the root slots. This recipe is what a running Trinix
# has, and the two must agree about on-disk features — see the -O argument in
# image/scripts/build-image.sh.

RECIPE_SOURCE="btrfs-progs"

# Answered explicitly rather than probed, per base/recipes/README.md: every one
# of these is a PKG_CHECK_MODULES in configure.ac that would otherwise make the
# rootfs depend on which recipes happened to run first.
#
#   util-linux  libblkid and libuuid — both mandatory, no flag to decline them.
#   zlib        mandatory too (btrfs-restore of zlib-compressed extents).
#   zstd        not mandatory, but it is the compression /data is mounted with,
#               so `btrfs receive` and `mkfs.btrfs --compress` need it to be
#               able to read what this system writes.
#
# Deliberately absent: e2fsprogs. btrfs-convert would link libext2fs and
# libcom_err; --disable-convert below says why we do not want it.
RECIPE_DEPENDS="glibc-runtime util-linux zlib zstd"

trinix_build() {
    # ⚠ btrfs-progs has no VPATH. Its Makefile is checked into the tree rather
    # than generated, and there is no $(srcdir) anywhere in it — only
    # Makefile.inc and config.h come from config.status. So running
    # "$SRCDIR/configure" from an empty $BUILDDIR produces a configured build
    # directory with nothing in it to build, and the failure reads as a missing
    # Makefile rather than as "this package cannot build out of tree".
    #
    # The source tree is shared between both architectures, so building in it
    # directly would let arm64 and x86_64 write objects over each other. Build
    # from a copy instead — the same problem zstd has, solved the same way.
    cp -a "$SRCDIR/." "$BUILDDIR/"

    ./configure \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        `# Everything upstream installs goes to $(bindir); there is no sbin` \
        `# split to fold away. Named anyway so a future change to the` \
        `# Makefile cannot quietly put fsck.btrfs somewhere /usr/bin is not.` \
        --bindir=/usr/bin \
        --sbindir=/usr/bin \
        \
        `# ⚠ "detect", and therefore exactly the auto-detected optional` \
        `# dependency base/recipes/README.md warns about: zoned support turns` \
        `# itself on whenever the linux headers are >= 5.10, which ours always` \
        `# are. Trinix has no SMR or ZNS device in its hardware story, so the` \
        `# honest answer is no rather than "whatever the container had".` \
        --disable-zoned \
        \
        `# ⚠ This one is *enabled* by default, not auto — leaving it alone` \
        `# makes configure hard-fail on a missing liblzo2, which is not a` \
        `# Trinix recipe and would be one more component in a base set that is` \
        `# deliberately ~40-60 of them. The cost is narrow: /data is mounted` \
        `# compress=zstd, so nothing on a Trinix disk is ever an LZO extent,` \
        `# and the *kernel* still decompresses LZO regardless (BTRFS_FS` \
        `# selects LZO_DECOMPRESS) so a foreign disk still mounts. What is` \
        `# lost is btrfs-restore and btrfs-receive on LZO data — offline` \
        `# paths, on data this system does not produce.` \
        --disable-lzo \
        \
        `# ...and its opposite. zstd is the mount default, so the offline` \
        `# tools have to be able to read it.` \
        --enable-zstd \
        \
        `# btrfs-convert rewrites an ext2/3/4 filesystem into a Btrfs one in` \
        `# place. Tempting for migrating an existing machine's /data and` \
        `# wrong for it: the result is one flat top-level subvolume, which is` \
        `# precisely the layout the subvolume split exists to avoid, and it` \
        `# would still need every file moved into @home/@apps/@var afterwards.` \
        `# image/README.md § Migration argues the whole case. Declining it` \
        `# also keeps libext2fs and libcom_err out of this binary.` \
        --disable-convert \
        \
        `# Multipath device-path resolution, and the only thing libudev is` \
        `# wanted for here. Multipath is a SAN feature; Trinix is a laptop.` \
        `# Left on, it would link systemd's libudev or not depending on` \
        `# whether systemd had already been built.` \
        --disable-libudev \
        \
        `# The checksum implementations. Builtin is already the default, but` \
        `# saying so means openssl — which *is* in the sysroot — cannot become` \
        `# a link-time dependency of mkfs.btrfs by accident. crc32c is the` \
        `# csum this image uses; the others (xxhash, sha256, blake2) are` \
        `# there so a disk formatted elsewhere can be read.` \
        --with-crypto=builtin \
        \
        `# Sphinx, and Python bindings for libbtrfsutil. Neither is a thing an` \
        `# immutable image ships, and both would put a build-container Python` \
        `# on the dependency list.` \
        --disable-documentation \
        --disable-python \
        \
        `# No .a in the image: nothing links btrfs statically. The programs` \
        `# still build libbtrfsutil.a as an intermediate and link it in — that` \
        `# is a Makefile prerequisite, not an install decision — so the shared` \
        `# libbtrfsutil.so exists purely for third parties.` \
        --disable-static \
        \
        `# ⚠ Kept, deliberately. btrfs's assertion handler prints a backtrace,` \
        `# and the place it fires is "btrfs check on somebody's failing disk,` \
        `# read off a serial console in Recovery" — the one situation where a` \
        `# frame list is the difference between a bug report and a shrug.` \
        --enable-backtrace

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # libbtrfs is the old, deprecated library: four symbols, all of them
    # send-stream helpers, superseded by libbtrfsutil. Upstream still builds
    # it for the handful of out-of-tree consumers that predate the split;
    # nothing in Trinix is one of them, and the programs link libbtrfsutil.a
    # statically, so removing it costs nothing that ships.
    #
    # libbtrfsutil.so stays: docs/plan/10's Rewind is C#, and
    # subvolume-create / snapshot / delete through P/Invoke is a considerably
    # better contract than parsing the output of btrfs(8).
    rm -f "$DESTDIR"/usr/lib/libbtrfs.so*
    rm -rf "$DESTDIR/usr/include/btrfs"
    rm -f "$DESTDIR"/usr/lib/*.a

    rm -rf "$DESTDIR/usr/share/man" "$DESTDIR/usr/share/bash-completion"
}

trinix_check() {
    local missing=''
    for required in btrfs mkfs.btrfs btrfstune btrfs-image fsck.btrfs btrfsck; do
        [ -e "$DESTDIR/usr/bin/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "btrfs-progs: missing$missing" >&2; return 1; }

    # --disable-convert took. If it did not, this binary links libext2fs and
    # libcom_err from e2fsprogs — a second, differently-versioned copy of the
    # ext2 library in an image that already has e2fsprogs' own.
    [ ! -e "$DESTDIR/usr/bin/btrfs-convert" ] \
        || { echo 'btrfs-progs: btrfs-convert was built — --disable-convert did not take' >&2; return 1; }

    [ ! -e "$DESTDIR/usr/lib/libbtrfs.so.0" ] \
        || { echo 'btrfs-progs: the deprecated libbtrfs still ships' >&2; return 1; }

    # The supported library, and its SONAME rather than merely a file with the
    # right name — a dependant records DT_SONAME, and a symlink created by the
    # install rule proves nothing about what that will say.
    local util="$DESTDIR/usr/lib/libbtrfsutil.so.1"
    [ -e "$util" ] \
        || { echo 'btrfs-progs: libbtrfsutil.so.1 missing — Rewind has nothing to P/Invoke into' >&2; return 1; }
    "$READELF" --dynamic "$util" | grep -q 'SONAME.*libbtrfsutil\.so\.1' \
        || { echo 'btrfs-progs: libbtrfsutil.so.1 carries no SONAME' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$DESTDIR/usr/bin/mkfs.btrfs" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "btrfs-progs: mkfs.btrfs is '$machine', wrong for $TRINIX_ARCH" >&2; return 1 ;;
    esac

    # Run it. mkfs.btrfs --version prints the release and then the feature
    # line that every --enable/--disable above lands in, which makes one
    # invocation answer both "is this the tarball sources.json pinned" and
    # "did configure do what the flags said" — and proves the binary starts,
    # which no amount of readelf does.
    #
    # The loader is invoked explicitly with a --library-path because the
    # staging tree is not in $SYSROOT yet (install_recipe runs after this),
    # and because ld.so.cache in a cross-build container belongs to Debian.
    local loader
    case "$TRINIX_ARCH" in
        arm64)  loader='ld-linux-aarch64.so.1' ;;
        x86_64) loader='ld-linux-x86-64.so.2'  ;;
        *) echo "btrfs-progs: unknown architecture '$TRINIX_ARCH'" >&2; return 1 ;;
    esac

    local banner
    banner="$("$QEMU" -L "$SYSROOT" "$SYSROOT/usr/lib/$loader" \
                  --library-path "$DESTDIR/usr/lib:$SYSROOT/usr/lib" \
                  "$DESTDIR/usr/bin/mkfs.btrfs" --version 2>&1)" \
        || { echo "btrfs-progs: mkfs.btrfs did not run under qemu-user: $banner" >&2; return 1; }

    local pinned
    pinned="$(trinix-fetch --version btrfs-progs)"
    grep -q "part of btrfs-progs v$pinned\$" <<< "$banner" \
        || { echo "btrfs-progs: sources.json pins $pinned but the binary says '$(head -n1 <<< "$banner")'" >&2; return 1; }

    # +ZSTD is the one that matters: /data is mounted compress=zstd, and a
    # btrfs-receive that cannot decompress what this system sends is a backup
    # that restores nothing. -LZO and -ZONED confirm the two declines above,
    # which is worth asserting because both defaulted to *on*.
    grep -q '+ZSTD' <<< "$banner" \
        || { echo "btrfs-progs: no zstd support — '$banner'" >&2; return 1; }
    grep -q -- '-LZO' <<< "$banner" \
        || { echo "btrfs-progs: lzo was linked in anyway — '$banner'" >&2; return 1; }
    grep -q -- '-ZONED' <<< "$banner" \
        || { echo "btrfs-progs: zoned support detected itself back on — '$banner'" >&2; return 1; }
}
