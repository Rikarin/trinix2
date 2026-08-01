# shellcheck shell=bash
# shadow — login, passwd, and the user database tools.
#
# This is what makes the Phase 2 exit criterion an honest one. util-linux's
# `login` has required PAM since 2.34, and PAM is not in the base image, so
# without shadow the only way to reach a shell on the serial console is
# autologin — which is not a login prompt, it is the absence of one.
#
# shadow's login authenticates against /etc/shadow through crypt(3) directly,
# which libxcrypt now provides. That is the whole dependency: no PAM stack, no
# NSS modules beyond `files`, and yescrypt as the hashing method rather than
# the SHA-crypt defaults that shipped when these tools were written.
#
# The util-linux recipe's header comment describes this gap as worth closing
# before anything resembling a release; this closes it.

RECIPE_SOURCE="shadow"
RECIPE_DEPENDS="glibc-runtime libxcrypt libcap"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --bindir=/usr/bin \
        --sbindir=/usr/bin \
        --sysconfdir=/etc \
        --disable-static \
        --disable-nls \
        --enable-shadowgrp \
        --enable-subordinate-ids \
        --with-yescrypt \
        --with-sha-crypt \
        `# Declined for the same reasons systemd declines them: not in the` \
        `# base image, and each one is a dependency and an attack surface.` \
        --without-libpam \
        --without-selinux \
        --without-audit \
        --without-acl \
        --without-attr \
        --without-libbsd \
        --without-btrfs \
        --without-nscd \
        --without-sssd \
        --without-bcrypt \
        --without-skey \
        --without-tcb \
        `# su is systemd's job here (systemd-run --machine=/machinectl shell),` \
        `# and shadow's needs setuid — which an immutable image should carry` \
        `# only where there is no alternative.` \
        --without-su \
        `# Nothing here is setuid: an immutable image should carry a setuid` \
        `# binary only where there is no alternative, and there is one —` \
        `# password changes go through systemd or a privileged service.` \
        --disable-account-tools-setuid \
        `# /var/log/lastlog is a sparse file indexed by UID, which on a` \
        `# read-only root has nowhere to live and no reader to care.` \
        --disable-lastlog \
        --disable-man

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la

    # shadow installs the account tools into /usr/sbin whatever --sbindir says.
    trinix_merge_usr

    # login.defs as installed is tuned for a multi-user Unix from 1995. What
    # matters for Trinix: yescrypt hashes, and a UID range that leaves room
    # below 1000 for the system users systemd's sysusers.d creates.
    install -d "$DESTDIR/etc"
    cat > "$DESTDIR/etc/login.defs" <<'EOF'
# Trinix login.defs — see login.defs(5).
#
# Deliberately short: systemd owns the parts of this file that used to matter
# (utmp, the environment, resource limits), and shadow reads only what is left.

MAIL_DIR                /var/mail

# yescrypt is libxcrypt's default and the strongest option shadow can emit.
ENCRYPT_METHOD          YESCRYPT

# Room below 1000 for systemd's sysusers.d allocations.
UID_MIN                 1000
UID_MAX                 60000
GID_MIN                 1000
GID_MAX                 60000
SYS_UID_MIN             100
SYS_UID_MAX             999
SYS_GID_MIN             100
SYS_GID_MAX             999

# Home directories live on /data, which is the only writable filesystem.
CREATE_HOME             yes
UMASK                   022
HOME_MODE               0700

USERGROUPS_ENAB         yes

# Failed-login delay: enough to make a serial console brute force pointless,
# short enough not to punish a typo.
FAIL_DELAY              3
LOGIN_RETRIES           5
LOGIN_TIMEOUT           60
EOF
}

trinix_check() {
    local missing=''
    for required in login passwd useradd usermod groupadd chpasswd; do
        [ -x "$DESTDIR/usr/bin/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "shadow: missing$missing" >&2; return 1; }

    # Without a crypt(3) provider, `login` builds fine and then rejects every
    # password at runtime — the worst possible place to find out.
    "$READELF" --dynamic "$DESTDIR/usr/bin/login" | grep -q 'libcrypt.so.2' \
        || { echo 'shadow: login is not linked against libcrypt — no password would ever verify' >&2; return 1; }
}
