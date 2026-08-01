# syntax=docker/dockerfile:1.10
#
# Phase 2 — the bootable disk image.
#
# Starts from the per-arch base image, which already contains the finished
# rootfs, and turns it into a GPT disk with an ESP, two root slots and a data
# partition. Nothing is mounted: see image/scripts/build-image.sh for why that
# matters and how it is avoided.
#
# Build via ./scripts/build.ps1 -Stage image -Arch <arch>

ARG BASE_IMAGE=trinix/base-arm64:dev

FROM ${BASE_IMAGE} AS image

ARG TRINIX_ARCH=arm64
ENV TRINIX_ARCH=${TRINIX_ARCH}

COPY image/scripts/build-image.sh /usr/local/lib/trinix/scripts/

# The intermediate partition images are a gigabyte of scratch that has no
# business in a layer; only $TRINIX_OUT is kept.
RUN --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/build-image.sh \
 && /usr/local/lib/trinix/scripts/build-image.sh "${TRINIX_ARCH}"

# ---------------------------------------------------------------------------
# Acceptance gate for the image's structure. The real exit criterion is a boot,
# which lives in scripts/run-vm.ps1 — this catches the failures that would make
# that boot silent.
# ---------------------------------------------------------------------------
FROM image AS image-verify

COPY image/scripts/image-sanity.sh /usr/local/lib/trinix/scripts/
RUN chmod +x /usr/local/lib/trinix/scripts/image-sanity.sh \
 && /usr/local/lib/trinix/scripts/image-sanity.sh "${TRINIX_ARCH}"

# ---------------------------------------------------------------------------
# What `--output type=local` extracts to the host: the image and its digest,
# and nothing else.
# ---------------------------------------------------------------------------
FROM scratch AS image-export
COPY --from=image /out/ /
