#!/usr/bin/env bash
# publish.sh <arm64|x86_64> <destdir> — publish Trinix's C# components.
#
# Runs in the build container with the .NET SDK, and produces a tree that is
# overlaid onto the target rootfs:
#
#   usr/lib/trinix/daemon/            trinixd and its dependencies
#   usr/lib/systemd/system/           its unit, and the symlink that enables it
#   usr/lib/powershell/Modules/       Trinix.Management
#
# Why this is a script and not a base recipe: recipes cross-compile pinned
# upstream tarballs with clang and a sysroot, and none of that applies here.
# `dotnet publish -r linux-arm64` is a first-class cross-compile that needs no
# sysroot at all, and BuildKit already invalidates this step correctly when a
# .cs file changes — which a recipe's stamp, keyed on the recipe directory,
# would not.

set -euo pipefail

arch="${1:?usage: publish.sh <arm64|x86_64> <destdir>}"
destdir="${2:?usage: publish.sh <arm64|x86_64> <destdir>}"
srcdir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

case "$arch" in
    arm64)  rid='linux-arm64' ;;
    x86_64) rid='linux-x64'   ;;
    *) echo "publish.sh: unknown architecture '$arch'" >&2; exit 1 ;;
esac

echo "==> publishing Trinix C# components for $rid"

# Framework-dependent, deliberately. See Trinix.Daemon.csproj: NativeAOT cannot
# cross-compile between architectures, and one build container serving both
# targets is the property this whole tree is organised around.
common=(
    --configuration Release
    --runtime "$rid"
    --no-self-contained
    -p:PublishSingleFile=false
    -p:DebugType=none
    -p:InvariantGlobalization=true
)

daemon_out="$destdir/usr/lib/trinix/daemon"
compositor_out="$destdir/usr/lib/trinix/compositor"
module_out="$destdir/usr/lib/powershell/Modules/Trinix.Management"
install -d "$daemon_out" "$compositor_out" "$module_out" "$destdir/usr/lib/systemd/system"

dotnet publish "$srcdir/Trinix.Daemon/Trinix.Daemon.csproj" "${common[@]}" --output "$daemon_out"

# The compositor. Its native half — libtrinix-wlr — is not here: that is a base
# recipe, because it cross-compiles against a sysroot containing wlroots, which
# is exactly what this stage cannot do and what a recipe is for. The two meet
# at /usr/lib, where the loader finds the library by soname.
dotnet publish "$srcdir/Trinix.Compositor/Trinix.Compositor.csproj" "${common[@]}" --output "$compositor_out"

# The module is loaded by pwsh, which is architecture-specific only in that it
# has to be able to load the assembly; publishing with the same RID keeps the
# two in step and avoids shipping a second copy of the runtime.
dotnet publish "$srcdir/Trinix.Management/Trinix.Management.csproj" "${common[@]}" --output "$module_out"

# A published module directory is full of framework assemblies that pwsh
# already has. Only the module itself and its manifest belong in the image.
find "$module_out" -mindepth 1 -maxdepth 1 \
    ! -name 'Trinix.Management.dll' \
    ! -name 'Trinix.Management.psd1' \
    -exec rm -rf {} +

install -d "$destdir/usr/lib/trinix"
install -m644 "$srcdir/Trinix.Management/selftest.ps1" "$destdir/usr/lib/trinix/selftest.ps1"

install -m644 "$srcdir/Trinix.Daemon/trinixd.service" "$destdir/usr/lib/systemd/system/trinixd.service"
install -m644 "$srcdir/Trinix.Management/trinix-selftest.service" \
              "$destdir/usr/lib/systemd/system/trinix-selftest.service"
install -m644 "$srcdir/Trinix.Compositor/trinix-compositor.service" \
              "$destdir/usr/lib/systemd/system/trinix-compositor.service"

# Enabled here rather than by `systemctl enable` on the target, for the same
# reason every other unit is: /etc is read-only on a running system, so the
# symlink has to exist in the image. It goes under /usr because the unit is
# part of the image rather than a local decision.
install -d "$destdir/usr/lib/systemd/system/multi-user.target.wants" \
           "$destdir/usr/lib/systemd/system/graphical.target.wants"
for unit in trinixd.service trinix-selftest.service; do
    ln -sfn "../$unit" "$destdir/usr/lib/systemd/system/multi-user.target.wants/$unit"
done
# The compositor belongs to graphical.target, which is what makes booting to a
# text console a matter of choosing a different target rather than editing
# anything.
ln -sfn ../trinix-compositor.service \
        "$destdir/usr/lib/systemd/system/graphical.target.wants/trinix-compositor.service"

chmod 755 "$daemon_out/trinixd" "$compositor_out/trinix-compositor"

echo "==> published $(du -sh "$daemon_out" | cut -f1) of daemon," \
     "$(du -sh "$compositor_out" | cut -f1) of compositor," \
     "$(du -sh "$module_out" | cut -f1) of module"
