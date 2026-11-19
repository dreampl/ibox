#!/usr/bin/env bash
# Sets up Swift 6 + xtool inside WSL (Ubuntu 24.04) and runs the IBOXCore tests.
# Does NOT run `xtool setup` (that needs an Xcode.xip and your Apple ID).
#
# From Windows:
#   wsl -d Ubuntu-24.04 -u root -- bash "/mnt/h/Jacki Brain/connect/.claude/worktrees/ios-xtool-9a94a1/src/JackiVpn.iOS/scripts/wsl-setup.sh" deps
#   wsl -d Ubuntu-24.04 -- bash "/mnt/h/Jacki Brain/connect/.claude/worktrees/ios-xtool-9a94a1/src/JackiVpn.iOS/scripts/wsl-setup.sh" tools
#   wsl -d Ubuntu-24.04 -- bash "/mnt/h/Jacki Brain/connect/.claude/worktrees/ios-xtool-9a94a1/src/JackiVpn.iOS/scripts/wsl-setup.sh" test
#
# Downloads: apt packages ~150 MB, Swift toolchain ~1 GB (~3 GB installed), xtool ~50 MB. All in the WSL disk on C:.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$HOME/ibox-ios"          # build in the Linux filesystem: faster than /mnt/h and keeps H: free
SWIFT_VERSION="${SWIFT_VERSION:-6.2}"
TOOLCHAIN="$HOME/.local/swift-$SWIFT_VERSION"
swift_env() { export PATH="$TOOLCHAIN/usr/bin:$HOME/.local/bin:$PATH"; }

case "${1:-}" in
deps)
    [ "$(id -u)" = 0 ] || { echo "run 'deps' as root (wsl -u root)"; exit 1; }
    apt-get update
    # Swift runtime deps (per swift.org Ubuntu 24.04 list) + FUSE for the xtool AppImage + usbmuxd for devices.
    DEBIAN_FRONTEND=noninteractive apt-get install -y \
        binutils git gnupg2 libc6-dev libcurl4-openssl-dev libedit2 libgcc-13-dev libncurses-dev \
        libpython3-dev libsqlite3-0 libstdc++-13-dev libxml2-dev libz3-dev pkg-config tzdata unzip zlib1g-dev \
        curl ca-certificates libfuse2t64 usbmuxd rsync
    ;;
tools)
    # Direct, resumable toolchain download (swiftly's downloader timed out on this network).
    if [ ! -x "$TOOLCHAIN/usr/bin/swift" ]; then
        name="swift-${SWIFT_VERSION}-RELEASE"
        url="https://download.swift.org/swift-${SWIFT_VERSION}-release/ubuntu2404/${name}/${name}-ubuntu24.04.tar.gz"
        mkdir -p "$HOME/.cache/ibox-ios"
        tarball="$HOME/.cache/ibox-ios/${name}.tar.gz"
        curl -fL --retry 10 --retry-all-errors --retry-delay 5 -C - -o "$tarball" "$url"
        mkdir -p "$TOOLCHAIN"
        tar -xzf "$tarball" -C "$TOOLCHAIN" --strip-components=1
        rm -f "$tarball"
    fi
    swift_env
    swift --version

    mkdir -p "$HOME/.local/bin"
    if [ ! -x "$HOME/.local/bin/xtool" ]; then
        curl -fsSL -o "$HOME/.local/bin/xtool" \
            "https://github.com/xtool-org/xtool/releases/latest/download/xtool-$(uname -m).AppImage"
        chmod +x "$HOME/.local/bin/xtool"
    fi
    "$HOME/.local/bin/xtool" --help | head -5
    echo "Next (needs you): xtool setup  -> Apple ID sign-in + path to Xcode.xip"
    ;;
test)
    swift_env
    mkdir -p "$WORK"
    rsync -a --delete --exclude .build --exclude xtool "$SRC/" "$WORK/"
    cd "$WORK"
    swift build --target IBOXCore
    swift test
    ;;
ipa)
    # After `xtool setup` has installed the Darwin SDK.
    swift_env
    mkdir -p "$WORK"
    rsync -a --delete --exclude .build --exclude xtool "$SRC/" "$WORK/"
    cd "$WORK"
    "$HOME/.local/bin/xtool" dev build --ipa
    ls -la xtool/*.ipa 2>/dev/null || find . -name '*.ipa' -newer Package.swift
    ;;
*)
    echo "usage: $0 deps|tools|test|ipa"; exit 2 ;;
esac
