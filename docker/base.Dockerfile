# syntax=docker/dockerfile:1.10
#
# Phase 2 — the base system.
#
# Starts from the per-arch toolchain image, so the sysroot and the
# self-configuring clang driver are already in place, and cross-builds the base
# recipe set into a clean rootfs.
#
# Build via ./scripts/build.ps1 -Stage base -Arch <arch>

ARG TOOLCHAIN_IMAGE=trinix/toolchain-arm64:dev

FROM ${TOOLCHAIN_IMAGE} AS base

ARG TRINIX_ARCH=arm64
ENV TRINIX_ARCH=${TRINIX_ARCH}
ENV TRINIX_ROOTFS=/opt/trinix/rootfs
ENV TRINIX_RECIPES=/usr/local/lib/trinix/recipes

# The manifest is re-copied here rather than inherited from the toolchain image,
# so bumping a base component's pin actually reaches this stage. The driver's
# per-recipe stamps then rebuild only the recipe whose version changed.
COPY base/sources.json /usr/local/share/trinix/sources.json

COPY base/scripts/build-base.sh /usr/local/lib/trinix/scripts/

# Recipes are copied last and as their own layer: adding or editing one must not
# rebuild anything above it, and the driver's per-recipe stamps mean only the
# changed recipe is actually rebuilt inside the layer.
COPY base/recipes /usr/local/lib/trinix/recipes

RUN --mount=type=bind,from=sources,target=/sources-seed,ro \
    --mount=type=cache,target=/sources,sharing=locked \
    --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/build-base.sh \
 && trinix-seed-sources \
 && /usr/local/lib/trinix/scripts/build-base.sh "${TRINIX_ARCH}"

# ---------------------------------------------------------------------------
# Phase 2 acceptance gate for the rootfs itself. The real exit criterion is a
# boot, which needs QEMU rather than Docker — this checks the things that can be
# checked without one, so a broken rootfs is caught before the VM stage.
# ---------------------------------------------------------------------------
FROM base AS base-verify

COPY base/scripts/base-sanity.sh /usr/local/lib/trinix/scripts/
RUN --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/base-sanity.sh \
 && /usr/local/lib/trinix/scripts/base-sanity.sh "${TRINIX_ARCH}"
