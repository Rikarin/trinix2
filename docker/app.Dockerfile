# syntax=docker/dockerfile:1.10
#
# Phase 6 — application bundles, signed, packaged as .tdi distribution images.
#
# Starts from the build container rather than from a toolchain image, because
# nothing here is cross-compiled with clang: `dotnet publish -r linux-arm64` is
# a first-class cross-compile that needs no sysroot, and mkfs.erofs runs
# natively and writes an architecture-independent filesystem.
#
# The signing key arrives as a BuildKit secret. That is not decoration: a
# --build-arg is visible in `docker history`, a COPY is a layer that can be
# extracted from any image built on top, and a bind mount would leave the key
# readable to every later stage. A secret is present for exactly one RUN and
# leaves nothing behind.
#
# Build via ./scripts/build.ps1 -Stage app -Arch <arch>

ARG HOST_TOOLS_IMAGE=trinix/host-tools:dev

FROM ${HOST_TOOLS_IMAGE} AS app

ARG TRINIX_ARCH=arm64
# Every timestamp inside a distribution image comes from here. Zero by default,
# so that an image built today and one built next month differ only where a
# signature makes them differ.
ARG SOURCE_DATE_EPOCH=0

COPY global.json /work/global.json
COPY src /work/src

RUN --mount=type=cache,target=/nuget,sharing=locked \
    --mount=type=secret,id=trinix-signing-certificate,target=/run/secrets/signing-certificate \
    --mount=type=secret,id=trinix-signing-key,target=/run/secrets/signing-key \
    chmod +x /work/src/pack-apps.sh \
 && TRINIX_SIGNING_CERTIFICATE=/run/secrets/signing-certificate \
    TRINIX_SIGNING_KEY=/run/secrets/signing-key \
    SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
    /work/src/pack-apps.sh "${TRINIX_ARCH}" /apps

# ---------------------------------------------------------------------------
# Acceptance gate. The Phase 6 exit criterion needs a VM — this is the half of
# it that does not: that what was just signed verifies, and that each specific
# way of tampering with it is detected.
#
# The trust store arrives as a build context rather than being baked in, for the
# same reason it is a directory in the system image rather than a compiled-in
# constant: which roots are trusted is a deployment decision, and a development
# root belongs to the developer who generated it.
# ---------------------------------------------------------------------------
FROM app AS app-verify

RUN --mount=type=bind,from=trust,target=/trust,ro \
    chmod +x /work/src/app-sanity.sh \
 && /work/src/app-sanity.sh /apps /trust

# ---------------------------------------------------------------------------
# What `--output type=local` extracts: the distribution images and the bundle
# trees they were built from. The .app trees are kept deliberately — they are
# what a developer opens when a signature is refused, and they cost nothing
# because the .tdi holds the same bytes.
# ---------------------------------------------------------------------------
FROM scratch AS app-export
COPY --from=app /apps/ /
