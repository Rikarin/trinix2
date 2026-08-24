# syntax=docker/dockerfile:1.10
#
# Phase 2 — the base system. Phase 3 adds Trinix's own C# components on top.
#
# Starts from the per-arch toolchain image, so the sysroot and the
# self-configuring clang driver are already in place, and cross-builds the base
# recipe set into a clean rootfs.
#
# Build via ./scripts/build.ps1 -Stage base -Arch <arch>

ARG TOOLCHAIN_IMAGE=trinix/toolchain-arm64:dev

# ---------------------------------------------------------------------------
# Trinix's own C# — trinixd and the Trinix.Management module.
#
# A stage rather than a base recipe, and the distinction is real: recipes
# cross-compile pinned upstream tarballs with clang against a sysroot, while
# `dotnet publish -r linux-arm64` is a first-class cross-compile that needs
# neither. The deciding argument is staleness. A recipe is rebuilt when its
# own directory changes; BuildKit rebuilds this when a .cs file does, which is
# the behaviour source code actually needs.
#
# Its own stage, and early, so that editing C# does not rebuild the base system
# and vice versa.
# ---------------------------------------------------------------------------
FROM ${TOOLCHAIN_IMAGE} AS dotnet-apps

ARG TRINIX_ARCH=arm64

COPY global.json /work/global.json
# The vendored Vixen packages, which src/NuGet.config names as ../vendor/vixen.
# Copied as its own layer and before src/, so that editing C# does not invalidate
# five megabytes of packages that did not change.
COPY vendor /work/vendor
COPY src /work/src

# NUGET_PACKAGES is set in the build container; the cache mount keeps restores
# off the network on every rebuild without baking packages into a layer.
RUN --mount=type=cache,target=/nuget,sharing=locked \
    chmod +x /work/src/publish.sh \
 && /work/src/publish.sh "${TRINIX_ARCH}" /publish

FROM ${TOOLCHAIN_IMAGE} AS base

ARG TRINIX_ARCH=arm64
ARG TRINIX_TRIPLE=aarch64-trinix-linux-gnu
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

# Trinix's own components land in the finished rootfs. Last, so that they
# overlay the base rather than being something the base has to know about —
# and after the driver's layout finalisation, since nothing here belongs in
# the mutable directories it relocates.
COPY --from=dotnet-apps /publish/ /opt/trinix/rootfs/${TRINIX_TRIPLE}/

# ---------------------------------------------------------------------------
# The trust store: the certificate authorities this image will accept signed
# applications from.
#
# Part of the image rather than of the writable half, and that is the whole
# mechanism. The root filesystem is read-only and replaced as a unit by an A/B
# update, so adding a trusted root means shipping a new image — which is itself
# signed. There is deliberately no way to add one at runtime.
#
# A named build context rather than a path in the repository, because these are
# generated per machine and never committed: scripts/build.ps1 stages
# signing/trusted/ (committed anchors) and signing/local/ (this developer's)
# into one directory and passes it as `trust`.
# ---------------------------------------------------------------------------
COPY --from=trust / /opt/trinix/rootfs/${TRINIX_TRIPLE}/usr/share/trinix/pki/roots/

# The reference application, as a signed distribution image, so that a booted
# system has something to install without a network. `trinix-bundle install`
# reads it from here; see docs/app-bundles.md.
COPY --from=apps *.tdi /opt/trinix/rootfs/${TRINIX_TRIPLE}/usr/share/trinix/applications/

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
