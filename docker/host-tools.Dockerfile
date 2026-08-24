# syntax=docker/dockerfile:1.10
#
# Stage 0 — the Trinix build container.
#
# This is the *only* thing that must exist on the host: no compilers, no SDKs, no
# cross-toolchains. The container is always built for the host's native
# architecture; targeting aarch64 and x86_64 is done by cross-compiling from
# inside it (running foreign containers under emulation is 10-20x slower).
#
# Build it with:  ./scripts/build.ps1 -Stage host-tools

ARG DEBIAN_TAG=trixie-slim

FROM debian:${DEBIAN_TAG} AS host-tools

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
ENV DEBIAN_FRONTEND=noninteractive
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

# ---------------------------------------------------------------------------
# Host packages.
#
# Deliberately grouped by why they are here, because this list will be audited
# often: anything that creeps in and ends up *linked into* a target binary is a
# reproducibility bug.
# ---------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
 && echo 'Binary::apt::APT::Keep-Downloaded-Packages "true";' > /etc/apt/apt.conf.d/keep-cache \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      `# host compilers: build LLVM, and the mini-GCC that builds glibc` \
      build-essential gcc g++ binutils make patch \
      `# autotools/meson/cmake — the union of what the base recipes need` \
      autoconf automake libtool pkg-config cmake ninja-build meson \
      bison flex gawk gettext m4 gperf texinfo help2man \
      `# gtk-doc.m4 only: some release tarballs (kmod) ship inconsistent` \
      `# autotools output and must be regenerated, and aclocal then needs the` \
      `# macros the project declares even though no documentation is built.` \
      gtk-doc-tools \
      `# scripting used by kernel/systemd/llvm build systems. The last three` \
      `# are Mesa's: it generates dispatch tables and driver descriptors from` \
      `# mako templates, reads a registry with yaml, and compares versions` \
      `# with packaging now that Python 3.12 has dropped distutils. Mesa's` \
      `# check for all three reports "Python >= 3.10 not found", which is` \
      `# true of none of them — hence naming them here.` \
      python3 python3-setuptools python3-jinja2 python3-pyelftools \
      python3-mako python3-yaml python3-packaging perl \
      `# fetch + unpack pinned sources` \
      curl ca-certificates git xz-utils bzip2 zstd unzip rsync file cpio bc jq \
      `# host -dev libs used while *building* tools, never shipped to the target` \
      libssl-dev libelf-dev zlib1g-dev libxml2-dev libncurses-dev \
      `# the .NET SDK hard-fails at startup without ICU (the SDK itself is not` \
      `# invariant-globalization, even though Trinix's own assemblies are)` \
      libicu-dev \
      `# kernel BTF + module tooling` \
      dwarves kmod \
      `# run foreign test binaries in-container (Phase 1 sanity suite)` \
      qemu-user-static \
      `# image assembly (Phase 2 onwards)` \
      fakeroot gdisk parted dosfstools e2fsprogs mtools \
      squashfs-tools erofs-utils cryptsetup-bin \
 && rm -rf /var/log/apt

# ---------------------------------------------------------------------------
# .NET SDK — installed from the official script rather than distro packages so
# the version is ours to pin, independent of Debian's release cadence.
# ---------------------------------------------------------------------------
ARG DOTNET_CHANNEL=10.0
ENV DOTNET_ROOT=/usr/share/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV NUGET_PACKAGES=/nuget
ENV PATH=/usr/share/dotnet:/root/.dotnet/tools:$PATH

RUN --mount=type=cache,target=/nuget,sharing=locked \
    curl --fail --location --silent --show-error https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_ROOT}" --no-path \
 && rm /tmp/dotnet-install.sh \
 && dotnet --version

# PowerShell: the distro's native shell, and the language the build scripts are
# written in — so the container can run scripts/*.ps1 in CI exactly as the Mac does.
RUN --mount=type=cache,target=/nuget,sharing=locked \
    dotnet tool install --global PowerShell \
 && pwsh --version

# ---------------------------------------------------------------------------
# Trinix layout inside the container.
# ---------------------------------------------------------------------------
ENV TRINIX_SOURCES=/sources
ENV TRINIX_TOOLCHAIN=/opt/trinix/toolchain
ENV TRINIX_SYSROOTS=/opt/trinix/sysroots
ENV TRINIX_BUILD=/build
ENV TRINIX_OUT=/out
ENV TRINIX_SOURCES_MANIFEST=/usr/local/share/trinix/sources.json
ENV PATH=/opt/trinix/toolchain/bin:$PATH

RUN mkdir -p "$TRINIX_SOURCES" "$TRINIX_TOOLCHAIN" "$TRINIX_SYSROOTS" \
             "$TRINIX_BUILD" "$TRINIX_OUT" /usr/local/share/trinix /work

COPY docker/scripts/trinix-fetch docker/scripts/trinix-extract docker/scripts/trinix-versions /usr/local/bin/
RUN chmod +x /usr/local/bin/trinix-fetch /usr/local/bin/trinix-extract /usr/local/bin/trinix-versions

# The pinned-source manifest is copied last: bumping a version invalidates only
# this layer, not the whole apt/.NET install above.
COPY base/sources.json ${TRINIX_SOURCES_MANIFEST}

# CMake toolchain files and meson cross files: together they make
# `clang --target=<triple> --sysroot=...` the uniform way every base recipe is
# built, whatever build system upstream happens to use.
COPY toolchain/cmake /usr/local/share/trinix/cmake
COPY toolchain/meson /usr/local/share/trinix/meson

WORKDIR /work
CMD ["/usr/local/bin/trinix-versions"]

# ---------------------------------------------------------------------------
# Phase 0 acceptance gate: the image must be able to cross-build for both
# target architectures. Built as a stage so `docker buildx build --target
# host-tools-verify` fails the build rather than merely printing a warning.
# ---------------------------------------------------------------------------
FROM host-tools AS host-tools-verify
RUN trinix-versions
