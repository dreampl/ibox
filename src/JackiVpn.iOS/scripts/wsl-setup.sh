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
    if ! command -v swiftly >/dev/null 2>&1 && [ ! -x "$HOME/.local/share/swiftly/bin/swiftly" ]; then
        tmp="$(mktemp -d)"
        curl -fsSL -o "$tmp/swiftly.tar.gz" "https://download.swift.org/swiftly/linux/swiftly-$(uname -m).tar.gz"
        tar -xzf "$tmp/swiftly.tar.gz" -C "$tmp"
        "$tmp/swiftly" init --quiet-shell-followup --assume-yes --skip-install
        rm -rf "$tmp"
    fi
    # shellcheck disable=SC1091
    . "${SWIFTLY_HOME_DIR:-$HOME/.local/share/swiftly}/env.sh"
    swiftly install --use --assume-yes "$SWIFT_VERSION"
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
    # shellcheck disable=SC1091
    . "${SWIFTLY_HOME_DIR:-$HOME/.local/share/swiftly}/env.sh"
    mkdir -p "$WORK"
    rsync -a --delete --exclude .build --exclude xtool "$SRC/" "$WORK/"
    cd "$WORK"
    swift build --target IBOXCore
    swift test
    ;;
ipa)
    # After `xtool setup` has installed the Darwin SDK.
    # shellcheck disable=SC1091
    . "${SWIFTLY_HOME_DIR:-$HOME/.local/share/swiftly}/env.sh"
    mkdir -p "$WORK"
    rsync -a --delete --exclude .build --exclude xtool "$SRC/" "$WORK/"
    cd "$WORK"
    "$HOME/.local/bin/xtool" dev build --ipa
    ls -la xtool/*.ipa 2>/dev/null || find . -name '*.ipa' -newer Package.swift
    ;;
*)
    echo "usage: $0 deps|tools|test|ipa"; exit 2 ;;
esac
