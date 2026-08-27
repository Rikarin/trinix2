# syntax=docker/dockerfile:1.10
#
# Phase 1 — the toolchain.
#
# Two kinds of stage live here:
#
#   llvm      Architecture-independent. One Clang/LLD install that targets every
#             Trinix architecture. Built once, shared by both sysroot builds.
#
#   toolchain Per architecture, selected with --build-arg TRINIX_ARCH. Linux
#             headers -> binutils -> mini-GCC -> glibc -> LLVM runtimes.
#             BuildKit caches each (stage, build-arg) pair separately, so
#             building arm64 does not invalidate x86_64.
#
# Each script is copied in immediately before the stage that runs it, never as
# one bulk COPY. That is deliberate: editing build-sysroot.sh must not
# invalidate the hour-long LLVM layer above it.
#
# Long compiles live in cache mounts, not in layers: the image ends up holding
# the *installed* toolchain and sysroots, not multi-gigabyte object trees. The
# cache mounts make an interrupted LLVM build resumable — ninja picks up where it
# stopped. The sysroot stages deliberately do not resume: each object dir carries
# a completion stamp, and a tree without one is discarded rather than built on,
# because resuming into a half-configured GCC is how every later attempt comes to
# die on `cannot execute 'cc1'`.
#
# Build via ./scripts/build.ps1 -Stage llvm,toolchain — it wires up the
# `sources` build context that seeds the download cache.

ARG HOST_TOOLS_IMAGE=trinix/host-tools:dev

FROM ${HOST_TOOLS_IMAGE} AS toolchain-base
COPY toolchain/scripts/trinix-toolchain-lib.sh toolchain/scripts/trinix-seed-sources \
     /usr/local/lib/trinix/scripts/
RUN chmod +x /usr/local/lib/trinix/scripts/trinix-seed-sources \
 && ln -sf /usr/local/lib/trinix/scripts/trinix-seed-sources /usr/local/bin/trinix-seed-sources

# ---------------------------------------------------------------------------
# LLVM / Clang / LLD — the long pole, and the only stage both architectures share.
# ---------------------------------------------------------------------------
FROM toolchain-base AS llvm

ARG LLVM_TARGETS="AArch64;X86;BPF"
ENV LLVM_TARGETS=${LLVM_TARGETS}

# LINK_JOBS defaults to 2: each LLVM link peaks around 4 GB and Docker Desktop
# commonly gets 8. Raise it on a build host with more memory.
ARG LINK_JOBS=2
ENV LINK_JOBS=${LINK_JOBS}

COPY toolchain/scripts/build-llvm.sh /usr/local/lib/trinix/scripts/
RUN --mount=type=bind,from=sources,target=/sources-seed,ro \
    --mount=type=cache,target=/sources,sharing=locked \
    --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/build-llvm.sh \
 && trinix-seed-sources && /usr/local/lib/trinix/scripts/build-llvm.sh

ENV PATH=/opt/trinix/toolchain/bin:$PATH

# ---------------------------------------------------------------------------
# Per-architecture sysroot.
#
#   ./scripts/build.ps1 -Stage toolchain -Arch x86_64
#
# The recipe is byte-identical for both architectures — TRINIX_ARCH is the only
# input. If a step ever needs an `if arm64` branch, that is a design smell worth
# arguing about before writing it.
# ---------------------------------------------------------------------------
FROM llvm AS toolchain

ARG TRINIX_ARCH=arm64
ENV TRINIX_ARCH=${TRINIX_ARCH}

COPY toolchain/scripts/build-sysroot.sh /usr/local/lib/trinix/scripts/
RUN --mount=type=bind,from=sources,target=/sources-seed,ro \
    --mount=type=cache,target=/sources,sharing=locked \
    --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/build-sysroot.sh \
 && trinix-seed-sources && /usr/local/lib/trinix/scripts/build-sysroot.sh "${TRINIX_ARCH}"

# ---------------------------------------------------------------------------
# Phase 1 acceptance gate.
# ---------------------------------------------------------------------------
FROM toolchain AS toolchain-verify

COPY toolchain/scripts/toolchain-sanity.sh /usr/local/lib/trinix/scripts/
RUN --mount=type=cache,target=/build,sharing=locked \
    chmod +x /usr/local/lib/trinix/scripts/toolchain-sanity.sh \
 && /usr/local/lib/trinix/scripts/toolchain-sanity.sh "${TRINIX_ARCH}"
