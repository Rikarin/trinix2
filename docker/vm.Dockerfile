# syntax=docker/dockerfile:1.10
#
# The VM tier.
#
# Docker cannot boot a kernel, so the second half of Trinix's testing story is
# QEMU — and QEMU itself runs from a container, which keeps the "Docker Desktop
# is the whole list" promise intact. UTM on the host is nicer for graphical
# work from Phase 4 onwards; it stays optional.
#
# Deliberately not built on top of host-tools: this image needs an emulator and
# two firmware blobs, not a cross-compiler, and keeping it separate means
# `run-vm.ps1` on a fresh clone pulls ~200 MB rather than rebuilding the
# toolchain.
#
# Used via ./scripts/run-vm.ps1 -Arch <arch>

ARG DEBIAN_TAG=trixie-slim

FROM debian:${DEBIAN_TAG} AS vm

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
ENV DEBIAN_FRONTEND=noninteractive

RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    rm -f /etc/apt/apt.conf.d/docker-clean \
 && echo 'Binary::apt::APT::Keep-Downloaded-Packages "true";' > /etc/apt/apt.conf.d/keep-cache \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      `# both target architectures, whatever the host happens to be` \
      qemu-system-arm qemu-system-x86 qemu-utils \
      `# UEFI firmware: Trinix boots through systemd-boot, not a BIOS stub` \
      qemu-efi-aarch64 ovmf \
 && rm -rf /var/log/apt

COPY image/scripts/run-qemu.sh /usr/local/bin/run-qemu
RUN chmod +x /usr/local/bin/run-qemu

WORKDIR /images
ENTRYPOINT ["/usr/local/bin/run-qemu"]
