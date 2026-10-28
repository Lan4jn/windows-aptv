#!/usr/bin/env bash
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${DIR}/.." && pwd)"

export DOTNET_CLI_HOME="${REPO_ROOT}/.tools"
export NUGET_PACKAGES="${REPO_ROOT}/.tools/nuget"
export DOTNET_ROOT="${REPO_ROOT}/.tools/dotnet"

echo "Executing APTV Windows candidate publish workflow..."
python3 "${DIR}/build_package.py"
