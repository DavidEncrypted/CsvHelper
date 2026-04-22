#!/usr/bin/env bash
#
# Development environment setup for CsvHelper.
#
# Installs the .NET 8 and .NET 9 SDKs via Microsoft's dotnet-install.sh into
# $HOME/.dotnet (no root required) and restores NuGet packages for the target
# frameworks buildable on this platform.
#
# Usage:
#   ./scripts/setup.sh           # install SDKs + restore
#   ./scripts/setup.sh --no-restore
#
# After running, either start a new shell (if you ran with `source`) or add the
# following to your shell profile:
#   export DOTNET_ROOT="$HOME/.dotnet"
#   export PATH="$DOTNET_ROOT:$PATH"

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"
INSTALL_SCRIPT="$DOTNET_INSTALL_DIR/dotnet-install.sh"
SKIP_RESTORE=0

for arg in "$@"; do
	case "$arg" in
		--no-restore) SKIP_RESTORE=1 ;;
		-h|--help)
			sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
			exit 0
			;;
		*) echo "Unknown argument: $arg" >&2; exit 2 ;;
	esac
done

log() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m==>\033[0m %s\n' "$*" >&2; }

log "Repository root: $REPO_ROOT"
log "Installing .NET SDKs to: $DOTNET_INSTALL_DIR"

mkdir -p "$DOTNET_INSTALL_DIR"

if [[ ! -x "$INSTALL_SCRIPT" ]]; then
	log "Downloading dotnet-install.sh"
	urls=(
		"https://dot.net/v1/dotnet-install.sh"
		"https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh"
		"https://raw.githubusercontent.com/dotnet/install-scripts/main/src/dotnet-install.sh"
	)
	downloaded=0
	for url in "${urls[@]}"; do
		if command -v curl >/dev/null 2>&1; then
			if curl -fsSL "$url" -o "$INSTALL_SCRIPT"; then downloaded=1; break; fi
		elif command -v wget >/dev/null 2>&1; then
			if wget -qO "$INSTALL_SCRIPT" "$url"; then downloaded=1; break; fi
		else
			echo "Need curl or wget to download dotnet-install.sh" >&2
			exit 1
		fi
		warn "Failed to fetch from $url, trying next mirror..."
	done
	if [[ "$downloaded" -ne 1 ]]; then
		echo "Could not download dotnet-install.sh from any mirror." >&2
		exit 1
	fi
	chmod +x "$INSTALL_SCRIPT"
fi

install_channel() {
	local channel="$1"
	log "Installing .NET SDK channel $channel"
	"$INSTALL_SCRIPT" --channel "$channel" --install-dir "$DOTNET_INSTALL_DIR" --no-path
}

install_channel 8.0
install_channel 9.0

export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

log "Installed SDKs:"
dotnet --list-sdks

if [[ "$SKIP_RESTORE" -eq 0 ]]; then
	# The library and test projects target several .NET Framework TFMs
	# (net462/net47/net48) which can't be built on Linux without Mono
	# reference assemblies. Restore only the cross-platform TFMs by passing
	# them explicitly; `dotnet test` later uses --framework to filter.
	log "Restoring NuGet packages (net8.0 and net9.0 only)"
	for tfm in net8.0 net9.0; do
		dotnet restore "$REPO_ROOT/src/CsvHelper/CsvHelper.csproj" \
			-p:TargetFrameworks="$tfm" -p:TargetFramework="$tfm"
		dotnet restore "$REPO_ROOT/tests/CsvHelper.Tests/CsvHelper.Tests.csproj" \
			-p:TargetFrameworks="$tfm" -p:TargetFramework="$tfm"
	done
fi

cat <<EOF

Setup complete. To use the installed SDKs in a new shell, add:

    export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
    export PATH="\$DOTNET_ROOT:\$PATH"

Run the tests with:

    ./scripts/test.sh           # all supported TFMs (net8.0, net9.0)
    ./scripts/test.sh net9.0    # single TFM

EOF
