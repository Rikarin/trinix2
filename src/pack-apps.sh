#!/usr/bin/env bash
# pack-apps.sh <arm64|x86_64> <destdir> — build, sign and package Trinix's applications.
#
# Produces one signed .tdi per application in $destdir, plus the .app tree it
# was built from (kept because it is what a developer inspects when a signature
# is refused, and it costs nothing — the .tdi holds the same bytes).
#
# Runs in the build container. It needs three things the base recipes do not:
# the .NET SDK, mkfs.erofs, and a private key. The key arrives through the
# environment, pointing at files that BuildKit mounts as secrets, so it is never
# an argument in a process listing and never a layer in an image.
#
# Why this is a script rather than a base recipe: the same reason src/publish.sh
# is. `dotnet publish -r linux-arm64` is a first-class cross-compile that needs
# no sysroot, and BuildKit invalidates this correctly when a .cs file changes —
# which a recipe's stamp, keyed on a recipe directory, would not.

set -euo pipefail

arch="${1:?usage: pack-apps.sh <arm64|x86_64> <destdir>}"
destdir="${2:?usage: pack-apps.sh <arm64|x86_64> <destdir>}"
srcdir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

: "${TRINIX_SIGNING_CERTIFICATE:?pack-apps.sh: TRINIX_SIGNING_CERTIFICATE is not set}"
: "${TRINIX_SIGNING_KEY:?pack-apps.sh: TRINIX_SIGNING_KEY is not set}"
[ -r "$TRINIX_SIGNING_CERTIFICATE" ] || { echo "pack-apps.sh: cannot read $TRINIX_SIGNING_CERTIFICATE" >&2; exit 1; }
[ -r "$TRINIX_SIGNING_KEY" ] || { echo "pack-apps.sh: cannot read $TRINIX_SIGNING_KEY" >&2; exit 1; }

case "$arch" in
    arm64)  rid='linux-arm64' ;;
    x86_64) rid='linux-x64'   ;;
    *) echo "pack-apps.sh: unknown architecture '$arch'" >&2; exit 1 ;;
esac

# Every timestamp that ends up inside an image comes from here, so that nothing
# about when the image was built leaks into it.
#
# That is not the same as a reproducible .tdi, and the difference is worth
# stating: the bundle inside carries a signature, ECDSA is randomised, and the
# manifest records when it was signed — so two builds of identical source
# produce two different images containing identical content. The Merkle root in
# the seal output is what to compare when asking whether two builds agree.
build_time="${SOURCE_DATE_EPOCH:-0}"

mkdir -p "$destdir"

# The signing tool, published once for the *container's own* architecture. It
# runs here, not on the target, so its RID is deliberately not $rid.
#
# Left behind rather than cleaned up, at a fixed path: the acceptance gate is a
# separate Dockerfile stage built on top of this one, and it needs the same tool
# to check what this produced. Publishing it twice would be three seconds and
# one more chance for the two to be different builds.
tool="${TRINIX_BUNDLE_TOOL:-/tmp/trinix-bundle-tool}"
echo "==> building the signing tool ($tool)"
dotnet publish "$srcdir/Trinix.Bundle.Tool/Trinix.Bundle.Tool.csproj" \
    --configuration Release --output "$tool" \
    -p:DebugType=none -p:GenerateDocumentationFile=false \
    --nologo --verbosity quiet
bundle_tool="$tool/trinix-bundle"

# pack_app <project file> <bundle metadata directory> <application name>
#
#   <bundle metadata directory>/Info.json      becomes Contents/Info.json
#   <bundle metadata directory>/Resources/     becomes Contents/Resources/
pack_app() {
    local project="$1" metadata="$2" name="$3"
    local app="$destdir/$name.app"

    echo "==> $name.app ($rid)"
    rm -rf "$app"
    install -d "$app/Contents/Bin" "$app/Contents/Resources"

    dotnet publish "$project" \
        --configuration Release --runtime "$rid" --no-self-contained \
        --output "$app/Contents/Bin" \
        -p:PublishSingleFile=false -p:DebugType=none -p:InvariantGlobalization=true \
        --nologo --verbosity quiet

    install -m644 "$metadata/Info.json" "$app/Contents/Info.json"
    if [ -d "$metadata/Resources" ]; then
        cp -a "$metadata/Resources/." "$app/Contents/Resources/"
    fi

    # An application bundle is content, not a build directory: the sealed file
    # list must not depend on whether the SDK happened to emit a .pdb.
    find "$app" -name '*.pdb' -delete

    "$bundle_tool" seal "$app" \
        --certificate "$TRINIX_SIGNING_CERTIFICATE" \
        --key "$TRINIX_SIGNING_KEY" \
        --architecture "$arch"

    "$bundle_tool" pack "$app" \
        --output "$destdir/$name.tdi" \
        --certificate "$TRINIX_SIGNING_CERTIFICATE" \
        --key "$TRINIX_SIGNING_KEY" \
        --build-time "@$build_time"
}

pack_app "$srcdir/Trinix.Apps.Hello/Trinix.Apps.Hello.csproj" \
         "$srcdir/Trinix.Apps.Hello/bundle" \
         'Hello'

echo
echo "==> packaged:"
ls -l "$destdir"/*.tdi
