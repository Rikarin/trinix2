# shellcheck shell=bash
# A font, because the image did not have one.
#
# Nothing in Phases 2–4 drew text: the console uses the kernel's built-in
# bitmap font and the compositor draws no glyphs at all. Vixen does, and it
# parses TrueType itself — there is no freetype and no fontconfig here, and
# none is wanted, because a UI framework that owns its own shaping and
# rasterisation is the whole reason its text is identical on three operating
# systems.
#
# What that costs is a *file*, at a path Vixen looks in. Vixen.Ui.Desktop's
# SystemFonts probes three, and this is the first: DejaVuSans.ttf under
# /usr/share/fonts/truetype/dejavu, which is Debian's layout.
#
# DejaVu rather than something prettier because it is a plain TrueType outline
# with wide coverage and a permissive licence. It is a placeholder for a
# system face chosen on design grounds — that decision belongs with the shell,
# in Phase 5, and it will be a different recipe rather than an edit to this one.

RECIPE_SOURCE="dejavu-fonts"
RECIPE_DEPENDS=""

trinix_build() {
    local dir="$DESTDIR/usr/share/fonts/truetype/dejavu"
    install -d "$dir"

    # ⚠ Not the whole tarball. It carries twenty-two faces, most of them
    # Condensed, ExtraLight or italic variants; these seven are the ones a user
    # interface and a terminal emulator ask for by name.
    local face missing=''
    for face in DejaVuSans DejaVuSans-Bold DejaVuSans-Oblique \
                DejaVuSansMono DejaVuSansMono-Bold \
                DejaVuSerif DejaVuSerif-Bold; do
        if [ -e "$SRCDIR/ttf/$face.ttf" ]; then
            install -m 644 "$SRCDIR/ttf/$face.ttf" "$dir/$face.ttf"
        else
            missing="$missing $face"
        fi
    done
    [ -z "$missing" ] || { echo "dejavu-fonts: not in the tarball:$missing" >&2; return 1; }

    install -d "$DESTDIR/usr/share/licenses/dejavu-fonts"
    install -m 644 "$SRCDIR/LICENSE" "$DESTDIR/usr/share/licenses/dejavu-fonts/LICENSE"
}

trinix_check() {
    local sans="$DESTDIR/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    [ -e "$sans" ] || { echo 'dejavu-fonts: DejaVuSans.ttf is not where Vixen looks' >&2; return 1; }

    # Vixen's parser reads a single face and rejects a collection, so the
    # useful assertion is about the format rather than the path: 0x00010000 is
    # a TrueType outline font, and "ttcf" would be a collection.
    local magic
    magic="$(od -An -tx1 -N4 "$sans" | tr -d ' \n')"
    [ "$magic" = '00010000' ] \
        || { echo "dejavu-fonts: $sans starts $magic, not a plain TrueType outline" >&2; return 1; }
}
