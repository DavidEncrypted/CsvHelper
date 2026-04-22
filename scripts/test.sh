#!/usr/bin/env bash
#
# Run the CsvHelper test suite for the cross-platform target frameworks.
#
# Usage:
#   ./scripts/test.sh              # runs net8.0 and net9.0
#   ./scripts/test.sh net9.0       # single TFM
#   ./scripts/test.sh net8.0 net9.0

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"

if [[ -d "$DOTNET_INSTALL_DIR" && ":$PATH:" != *":$DOTNET_INSTALL_DIR:"* ]]; then
	export DOTNET_ROOT="$DOTNET_INSTALL_DIR"
	export PATH="$DOTNET_ROOT:$PATH"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
	echo "dotnet not found on PATH. Run ./scripts/setup.sh first." >&2
	exit 1
fi

frameworks=("$@")
if [[ ${#frameworks[@]} -eq 0 ]]; then
	frameworks=(net8.0 net9.0)
fi

TEST_PROJ="$REPO_ROOT/tests/CsvHelper.Tests/CsvHelper.Tests.csproj"

status=0
for tfm in "${frameworks[@]}"; do
	printf '\n\033[1;34m==> Testing %s\033[0m\n' "$tfm"
	if ! dotnet test "$TEST_PROJ" \
		--configuration Release \
		--framework "$tfm" \
		-p:TargetFrameworks="$tfm" \
		-p:TargetFramework="$tfm"; then
		status=1
	fi
done

exit "$status"
