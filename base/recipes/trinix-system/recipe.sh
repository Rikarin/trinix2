# shellcheck shell=bash
# The distribution itself: identity, accounts, and the configuration that makes
# the assembled binaries into a system that boots.
#
# Everything here follows from one decision — the root filesystem is read-only.
# That is what the A/B image model requires, and it has consequences that are
# easy to discover the hard way at 3am on a serial console:
#
#   * Users cannot be created at first boot, because /etc cannot be written.
#     They are baked in below. systemd-sysusers still runs; it finds everything
#     already present and exits without writing, which is why the accounts here
#     must match the sysusers.d files the components ship.
#
#   * /var, /home and friends cannot live on the root filesystem. They are
#     symlinks onto /data, the one writable partition, and systemd-tmpfiles
#     populates them on first boot.
#
#   * /etc/machine-id is shipped uninitialised. systemd's documented behaviour
#     on a read-only /etc is to mount a tmpfs over it, so the ID is transient
#     until Phase 7 gives the image an installer that can seed one.

RECIPE_SOURCE=""          # synthetic: this is Trinix's own content
RECIPE_DEPENDS="glibc-runtime systemd shadow bash dash coreutils util-linux powershell dotnet"

# Nothing links against configuration, and the layout below actively disagrees
# with the sysroot's: /var here is a symlink onto the writable partition, while
# the sysroot has a real /var that other recipes install into.
RECIPE_ROOTFS_ONLY=1

# Bumped by hand; there is no release engineering yet (Phase 8).
TRINIX_VERSION='0.3.0'
TRINIX_VERSION_ID='0.3'
TRINIX_CODENAME='Phase 3'

# The default root password for a development image.
#
# It is deliberately a constant rather than a random value: the image is
# reproducible, and an unpredictable password in an image nobody can log into
# is worse than a well-known one in an image that is only ever booted in a VM.
# Phase 8's installer is where a real credential gets set; until then this is
# documented in the README rather than hidden here.
TRINIX_ROOT_PASSWORD="${TRINIX_ROOT_PASSWORD:-trinix}"

# The first user account. It exists in the image rather than being created by a
# setup assistant because there is no setup assistant until Phase 8, and an
# image whose only account is root would make PowerShell-as-the-login-shell
# untestable — root deliberately keeps a POSIX shell.
TRINIX_USER='trinix'
TRINIX_USER_UID=1000
TRINIX_USER_PASSWORD="${TRINIX_USER_PASSWORD:-trinix}"

trinix_build() {
    install -d "$DESTDIR"/etc "$DESTDIR"/usr/lib "$DESTDIR"/usr/share

    _identity
    _accounts
    _filesystems
    _console
    _powershell
    _units
}

# --- Who this system says it is --------------------------------------------
_identity() {
    # The canonical copy lives in /usr/lib: it belongs to the image, not to the
    # configuration, and on a system with a writable /etc that distinction is
    # what lets an update replace it.
    cat > "$DESTDIR/usr/lib/os-release" <<EOF
NAME="Trinix"
ID=trinix
PRETTY_NAME="Trinix $TRINIX_VERSION ($TRINIX_CODENAME)"
VERSION="$TRINIX_VERSION ($TRINIX_CODENAME)"
VERSION_ID=$TRINIX_VERSION_ID
VERSION_CODENAME=${TRINIX_CODENAME// /-}
ANSI_COLOR="1;36"
HOME_URL="https://github.com/rikarin/trinix"
SUPPORT_END=
IMAGE_ID=trinix
IMAGE_VERSION=$TRINIX_VERSION
ARCHITECTURE=$TRINIX_ARCH
EOF
    ln -sfn ../usr/lib/os-release "$DESTDIR/etc/os-release"

    printf 'trinix\n' > "$DESTDIR/etc/hostname"

    cat > "$DESTDIR/etc/hosts" <<'EOF'
127.0.0.1   localhost
::1         localhost ip6-localhost ip6-loopback
127.0.1.1   trinix
EOF

    # agetty prints this above the login prompt. \r is the kernel release and
    # \l the tty — enough to tell two serial consoles apart, and the first
    # confirmation that the image booted the kernel it was built with.
    cat > "$DESTDIR/etc/issue" <<'EOF'
Trinix \r \m (\l)

EOF

    # Shipped uninitialised on purpose — see the header comment.
    : > "$DESTDIR/etc/machine-id"
}

# --- Accounts ---------------------------------------------------------------
_accounts() {
    # UIDs and GIDs are fixed values, not "whatever useradd picked", because
    # two Trinix images of the same version must be byte-identical and because
    # /data survives an A/B update that replaces everything else. A file owned
    # by GID 6 has to still mean `disk` after the update.
    # root's shell is bash, not pwsh, and that is the whole rescue story: when
    # PowerShell is the thing that will not start, the account you use to fix
    # it must not depend on PowerShell starting. Users get pwsh; root gets a
    # shell that has no runtime, no JIT and no module path.
    cat > "$DESTDIR/etc/passwd" <<EOF
root:x:0:0:root:/root:/usr/bin/bash
daemon:x:1:1:daemon:/usr/sbin:/usr/bin/false
bin:x:2:2:bin:/bin:/usr/bin/false
sys:x:3:3:sys:/dev:/usr/bin/false
messagebus:x:18:18:D-Bus Message Bus:/nonexistent:/usr/bin/false
$TRINIX_USER:x:$TRINIX_USER_UID:$TRINIX_USER_UID:Trinix User:/home/$TRINIX_USER:/usr/bin/pwsh
nobody:x:65534:65534:Nobody:/:/usr/bin/false
EOF

    # The device groups are the ones udev's shipped rules chown nodes to. A
    # missing one is not fatal, it just makes udev log an error for every
    # matching device — which on a graphical system is every boot.
    #
    # _seatd is the entire authorisation model for the display: seatd runs as
    # root, owns the DRM device and every input device, and hands them out to
    # whoever can talk to its socket — which is this group and nothing else.
    # The compositor is in it, and that is why the compositor is not root.
    cat > "$DESTDIR/etc/group" <<EOF
root:x:0:
daemon:x:1:
bin:x:2:
sys:x:3:
adm:x:4:
tty:x:5:
disk:x:6:
lp:x:7:
mem:x:8:
kmem:x:9:
wheel:x:10:
cdrom:x:11:
dialout:x:12:
tape:x:13:
audio:x:14:
video:x:15:
render:x:16:
input:x:17:
messagebus:x:18:
kvm:x:19:
sgx:x:20:
systemd-journal:x:21:
_seatd:x:22:$TRINIX_USER
users:x:100:
$TRINIX_USER:x:$TRINIX_USER_UID:
nogroup:x:65534:
EOF

    # Password hashing happens on the build machine, so the hash is computed
    # with the *host's* crypt implementation rather than the one being built.
    # SHA-512 rather than login.defs' yescrypt default for exactly that reason:
    # it is the strongest method both sides are guaranteed to agree on. Only
    # the salt is fixed, so the image stays reproducible.
    local root_hash user_hash
    root_hash="$(openssl passwd -6 -salt trinixdev "$TRINIX_ROOT_PASSWORD")"
    user_hash="$(openssl passwd -6 -salt trinixusr "$TRINIX_USER_PASSWORD")"
    [ -n "$root_hash" ] && [ -n "$user_hash" ] \
        || { echo 'trinix-system: could not hash the default passwords' >&2; return 1; }

    # Field 3 is "days since epoch of last change". A literal 0 would mean
    # 1970, which shadow reads as "expired"; 20000 is a date safely in the past
    # that is not the epoch, and it keeps the file byte-identical between builds.
    {
        printf 'root:%s:20000:0:99999:7:::\n' "$root_hash"
        printf 'daemon:!*:20000::::::\n'
        printf 'bin:!*:20000::::::\n'
        printf 'sys:!*:20000::::::\n'
        printf 'messagebus:!*:20000::::::\n'
        printf '%s:%s:20000:0:99999:7:::\n' "$TRINIX_USER" "$user_hash"
        printf 'nobody:!*:20000::::::\n'
    } > "$DESTDIR/etc/shadow"
    chmod 600 "$DESTDIR/etc/shadow"

    awk -F: '{ printf "%s:!::\n", $1 }' "$DESTDIR/etc/group" > "$DESTDIR/etc/gshadow"
    chmod 600 "$DESTDIR/etc/gshadow"

    # The user's home directory, shipped rather than created at first boot.
    #
    # tmpfiles.d can create it and does — but only once /data is mounted and
    # sysinit has run, and a home directory that appears slightly later than
    # the first login prompt is a login that fails with "Unable to cd". So it
    # is built here instead: /home does not exist in the rootfs yet, so this
    # lands in the driver's factory image and is seeded into /data when the
    # disk image is assembled. The tmpfiles rule stays as the answer for a
    # /data that has been wiped.
    install -d -m 0700 -o "$TRINIX_USER_UID" -g "$TRINIX_USER_UID" \
        "$DESTDIR/home/$TRINIX_USER"

    # chsh and anything else that validates a login shell reads this. pwsh is
    # first because it is the default; the POSIX shells stay because /bin/sh
    # scripting and the rescue path both depend on them.
    cat > "$DESTDIR/etc/shells" <<'EOF'
/usr/bin/pwsh
/bin/sh
/usr/bin/sh
/usr/bin/dash
/usr/bin/bash
EOF
}

# --- Filesystems ------------------------------------------------------------
_filesystems() {
    # The mutable half of the system — /var, /home, /root and /Applications as
    # symlinks onto /data — is not created here. It is the stage driver's,
    # because recipes install into /var and the relocation that follows from
    # that can only happen once every recipe has run. See build-base.sh.

    # PARTLABEL rather than UUID: the labels are a property of the partition
    # layout, which is Trinix's own (see image/), while UUIDs are generated per
    # image. udev resolves these; the kernel gets the root device by PARTUUID
    # on the command line instead, because udev is not running yet at that point.
    cat > "$DESTDIR/etc/fstab" <<'EOF'
# <device>                 <mount>  <type>  <options>                             <dump> <fsck>
#
# The root filesystem is mounted by the kernel from root= on the command line
# and is deliberately absent here: which of the two slots is root changes with
# every A/B update, and fstab is part of the image being updated.
PARTLABEL=trinix-data      /data    ext4    defaults,noatime                      0 2
PARTLABEL=trinix-esp       /boot    vfat    ro,noatime,umask=0077,nofail          0 2
tmpfs                      /tmp     tmpfs   rw,nosuid,nodev,mode=1777,size=50%    0 0
EOF

    # Nothing writes this yet — networkd and resolved are both declined by the
    # systemd recipe. The symlink is here so that whatever eventually does
    # (Phase 8's network frontend) has a writable path to write it to.
    ln -sfn ../run/resolv.conf "$DESTDIR/etc/resolv.conf"

    # /etc/mtab has been "the kernel's view of what is mounted" rather than a
    # file for two decades, and enough tools still open it by name that its
    # absence is worth more than the one line it costs. It cannot be created at
    # boot here, because /etc is read-only.
    ln -sfn ../proc/self/mounts "$DESTDIR/etc/mtab"
}

# --- Console and shell environment -----------------------------------------
_console() {
    # /bin/sh reads this for login shells, and so does bash. Deliberately thin:
    # PowerShell's profile is where the user-facing environment gets built in
    # Phase 3, and duplicating it here would mean two places to change.
    cat > "$DESTDIR/etc/profile" <<'EOF'
# System-wide environment for POSIX login shells.

export PATH=/usr/bin
export LANG=C.UTF-8

# The terminfo database is trimmed to the terminals a Trinix system can
# actually meet (see base/recipes/ncurses); fall back rather than misbehave.
case "${TERM:-}" in
    ''|unknown) export TERM=linux ;;
esac

umask 022

# Components drop their environment here rather than editing this file: .NET
# needs DOTNET_ROOT and its telemetry opt-out, and a recipe that owns a
# variable should own the file that sets it. pwsh reads this too — it runs
# /etc/profile through sh when started as a login shell — which is how a
# PowerShell session inherits a POSIX-shaped environment.
for _profile in /etc/profile.d/*.sh; do
    [ -r "$_profile" ] && . "$_profile"
done
unset _profile

if [ "$(id -u)" = 0 ]; then
    PS1='\u@\h:\w# '
else
    PS1='\u@\h:\w$ '
fi
export PS1
EOF
}

# --- The interactive shell --------------------------------------------------
_powershell() {
    # $PSHOME/profile.ps1 — the all-users, all-hosts profile, which is the only
    # one that exists on a fresh system where /home is empty. It lives under
    # the powershell recipe's directory but belongs to the distribution, the
    # same way /etc/login.defs belongs to Trinix rather than to shadow.
    install -d "$DESTDIR/usr/lib/powershell"
    cat > "$DESTDIR/usr/lib/powershell/profile.ps1" <<'EOF'
# Trinix system-wide PowerShell profile.
#
# Deliberately short. A login shell that takes a second to start because its
# profile is doing clever things is a login shell people learn to dread, and
# pwsh's cold start is already the slowest part of reaching a prompt.

# The administration surface. Imported rather than left to autoloading so that
# Get-Command and tab completion know about it on the first keystroke.
Import-Module Trinix.Management -ErrorAction SilentlyContinue

# A prompt that answers "where am I and am I root", and nothing else. ~ for
# home is the one abbreviation worth the string work.
#
# No external commands: a prompt runs before every single line a user types,
# and forking `hostname` and `id` each time is both slower than it looks and
# fragile — the first version of this used them and rendered an empty hostname
# on a system where the binary was not where it expected.
$script:TrinixHost = [Environment]::MachineName
$script:TrinixMark = if ([Environment]::UserName -eq 'root') { '#' } else { '$' }

function prompt {
    $path = $PWD.Path
    if ($HOME -and $path.StartsWith($HOME)) { $path = '~' + $path.Substring($HOME.Length) }
    "$([char]27)[36m$([Environment]::UserName)@$script:TrinixHost$([char]27)[0m $path $script:TrinixMark "
}

# Conveniences a mac user reaches for without thinking. Aliases, not functions,
# where PowerShell already has the cmdlet.
Set-Alias -Name ll -Value Get-ChildItem -Option AllScope -ErrorAction SilentlyContinue
Set-Alias -Name which -Value Get-Command -Option AllScope -ErrorAction SilentlyContinue

if (Get-Module PSReadLine) {
    Set-PSReadLineOption -EditMode Emacs -PredictionSource History -BellStyle None
}
EOF

    # Where pwsh looks for modules that belong to the system rather than to a
    # user. Trinix.Management is installed here by the C# publish stage; this
    # only guarantees the directory exists so an empty install is not an error.
    install -d "$DESTDIR/usr/lib/powershell/Modules"
}

# --- What starts at boot ----------------------------------------------------
_units() {
    install -d "$DESTDIR/etc/systemd/system" \
               "$DESTDIR/etc/systemd/system/getty.target.wants" \
               "$DESTDIR/etc/systemd/system/multi-user.target.wants"

    # A read-only /etc means `systemctl enable` can never run on the target, so
    # every enablement symlink is made here. These are the same symlinks
    # `systemctl preset-all` would produce, written by hand because there is no
    # point at which the image is writable and running.
    #
    # graphical.target, from Phase 4 onwards. It requires multi-user.target, so
    # nothing that worked before stops working — the serial console, the
    # journal, trinixd and the self-test all still come up, and the compositor
    # is added on top. Booting without a display is `systemd.unit=multi-user.target`
    # on the kernel command line, which is a smaller thing to remember than an
    # edit to a read-only filesystem.
    ln -sfn /usr/lib/systemd/system/graphical.target "$DESTDIR/etc/systemd/system/default.target"

    # The virtual console, on tty2 rather than tty1. tty1 is where the
    # compositor draws, and seatd binds a session to whichever VT is active —
    # which at boot is tty1. A getty there would be writing text into the
    # framebuffer the compositor is composing into, and both would be right.
    #
    # The *serial* console is not enabled here: systemd's getty generator reads
    # console= from the kernel command line and instantiates serial-getty@ for
    # whatever it finds, which is what makes one image work on ttyAMA0 (arm64
    # virt) and ttyS0 (x86_64) without knowing which it booted on.
    ln -sfn /usr/lib/systemd/system/getty@.service \
            "$DESTDIR/etc/systemd/system/getty.target.wants/getty@tty2.service"

    # seatd, which is how the compositor gets the display and the keyboard
    # without being root. In multi-user rather than graphical.target.wants so
    # that a text boot still has a working seat for anything that wants one.
    ln -sfn /usr/lib/systemd/system/seatd.service \
            "$DESTDIR/etc/systemd/system/multi-user.target.wants/seatd.service"

    # The dynamic linker cache is a /etc/ld.so.cache that cannot be written and
    # would buy nothing if it could: every shared library in the image is in
    # /usr/lib, which the loader searches by default, and the set of libraries
    # cannot change without replacing the whole image. glibc's ldconfig is not
    # even shipped (see the glibc-runtime recipe), so left alone this unit
    # fails on every boot for a cache nothing is waiting for.
    ln -sfn /dev/null "$DESTDIR/etc/systemd/system/ldconfig.service"

    # systemd-boot's random seed is a real feature — it hands the kernel
    # entropy before userspace exists — and it needs a writable ESP to refresh
    # the seed on each boot. Trinix mounts the ESP read-only, because the
    # bootloader and the kernel are part of the signed image and an update is
    # the only thing that should be rewriting them. Revisit when Phase 7's
    # updater is writing to the ESP anyway and there is somewhere safe to put
    # a seed.
    ln -sfn /dev/null "$DESTDIR/etc/systemd/system/systemd-boot-random-seed.service"

    # Masked, not merely disabled. systemd-update-done answers the question
    # "has /usr changed since /etc was last reconciled with it", and reconciles
    # them by writing to /etc — which cannot happen here. On an image-based
    # system the question has no meaning either: /usr and /etc are replaced
    # together, as one image, or not at all.
    ln -sfn /dev/null "$DESTDIR/etc/systemd/system/systemd-update-done.service"

    # journald keeps its journal on /data once that is mounted; until then it
    # buffers in /run, which is why the flush is a separate unit upstream.
    install -d "$DESTDIR/etc/systemd"
    cat > "$DESTDIR/etc/systemd/journald.conf" <<'EOF'
[Journal]
Storage=persistent
# Off, after trying it on. Forwarding the journal to the console duplicates
# every message systemd already prints as a status line, and on an emulated
# serial port that traffic is slow enough to measurably delay the boot it is
# supposed to be reporting on. What made it unnecessary is that the units
# whose output actually has to be visible — trinix-selftest.service — ask for
# the console themselves with StandardOutput=journal+console, which is both
# more precise and free for everything else.
ForwardToConsole=no
MaxLevelConsole=warning
SystemMaxUse=256M
EOF

    # /data is empty on a fresh image. tmpfiles creates the rest of /var from
    # systemd's own var.conf; these are the directories that are Trinix's, not
    # systemd's, and the ones the symlinks above point at.
    install -d "$DESTDIR/usr/lib/tmpfiles.d"
    cat > "$DESTDIR/usr/lib/tmpfiles.d/trinix.conf" <<EOF
# The writable half of the system, created on first boot under /data.
d /data              0755 root root -
d /data/var          0755 root root -
d /data/home         0755 root root -
d /data/root         0700 root root -
d /data/srv          0755 root root -
d /data/opt          0755 root root -
d /data/Applications 0755 root root -

# The first user's home. It cannot be shipped in the image — /home is a symlink
# onto the partition that does not exist until first boot — so tmpfiles creates
# it, which is also what would happen for any later account.
d /data/home/$TRINIX_USER 0700 $TRINIX_USER $TRINIX_USER -
EOF
}

# _installed_in_sysroot <absolute path> — is this executable really there?
#
# The subtlety is symlinks with absolute targets, which both pwsh and dotnet
# are: /usr/bin/pwsh points at /usr/lib/powershell/pwsh, and the shell resolves
# that against the *build container's* root, where it does not exist. Testing
# -x on the link therefore says "missing" about a file that is present. One
# level of indirection is all these have, and all this follows.
_installed_in_sysroot() {
    local path="$1" target

    if [ -L "$SYSROOT$path" ]; then
        target="$(readlink "$SYSROOT$path")"
        case "$target" in
            /*) path="$target" ;;
            *)  path="${path%/*}/$target" ;;
        esac
    fi

    [ -x "$SYSROOT$path" ]
}

trinix_check() {
    local missing=''
    for required in etc/passwd etc/shadow etc/group etc/fstab etc/machine-id \
                    etc/os-release usr/lib/os-release; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    # -L rather than -e: the enablement symlinks point at absolute paths under
    # /usr, which resolve against the *build container's* root while this runs.
    # Testing them with -e asks whether the build container has a systemd, and
    # the answer is no.
    for required in etc/systemd/system/default.target \
                    etc/systemd/system/getty.target.wants/getty@tty2.service \
                    etc/systemd/system/multi-user.target.wants/seatd.service; do
        [ -L "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "trinix-system: missing$missing" >&2; return 1; }

    # Every login shell has to exist in the image, or the prompt is reached and
    # then immediately loses to "no such file or directory". Checked for every
    # account rather than just root, because the interesting one is now the
    # user whose shell is pwsh.
    local account shell
    while IFS=: read -r account _ _ _ _ _ shell; do
        case "$shell" in
            /usr/bin/false|'') continue ;;
        esac
        _installed_in_sysroot "$shell" \
            || { echo "trinix-system: $account's shell $shell is not in the sysroot" >&2; return 1; }
    done < "$DESTDIR/etc/passwd"

    # The Phase 3 exit criterion in static form: a user whose shell is
    # PowerShell, and a root account whose shell is not — so that the rescue
    # path does not depend on the thing being rescued.
    grep -q "^$TRINIX_USER:.*:/usr/bin/pwsh\$" "$DESTDIR/etc/passwd" \
        || { echo "trinix-system: $TRINIX_USER does not land in PowerShell" >&2; return 1; }
    grep -q '^root:.*:/usr/bin/bash$' "$DESTDIR/etc/passwd" \
        || { echo 'trinix-system: root no longer has a POSIX rescue shell' >&2; return 1; }

    # An empty second field would let anyone in without a password; a literal
    # '!' would let nobody in at all. Both are easy to produce by accident here.
    local hash
    hash="$(awk -F: '$1 == "root" { print $2 }' "$DESTDIR/etc/shadow")"
    case "$hash" in
        '$'*) ;;
        *) echo "trinix-system: root's password field is '$hash', which is not a hash" >&2; return 1 ;;
    esac
}
