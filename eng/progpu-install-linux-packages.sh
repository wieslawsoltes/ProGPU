#!/usr/bin/env bash
set -euo pipefail

# Each request is a package name, or alternatives written "first|second" for a
# package that is named differently between distribution releases. An installed
# alternative satisfies its request; otherwise the first alternative the package
# indexes know is installed.

if (( $# == 0 )); then
  echo "No Linux packages were requested." >&2
  exit 2
fi

package_is_installed() {
  local status
  status="$(dpkg-query -W -f='${Status}' "$1" 2>/dev/null || true)"
  [[ "${status}" == "install ok installed" ]]
}

package_is_known() {
  apt-cache show "$1" >/dev/null 2>&1
}

indexes_refreshed=0
refresh_package_indexes() {
  sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get update \
    -o Acquire::Retries=3 \
    -o Dpkg::Use-Pty=0
  indexes_refreshed=1
}

# Sets selected_alternative to the alternative to install, or leaves it empty
# when one of them is installed already.
select_alternative() {
  local request="$1" alternative
  local -a alternatives
  selected_alternative=
  if [[ "${request}" == '|'* || "${request}" == *'|' || "${request}" == *'||'* ]]; then
    echo "The Linux package alternatives '${request}' contain an empty name." >&2
    exit 2
  fi
  IFS='|' read -r -a alternatives <<< "${request}"
  for alternative in "${alternatives[@]}"; do
    if package_is_installed "${alternative}"; then
      return 0
    fi
  done
  while true; do
    for alternative in "${alternatives[@]}"; do
      if package_is_known "${alternative}"; then
        selected_alternative="${alternative}"
        return 0
      fi
    done
    if (( indexes_refreshed == 1 )); then
      echo "None of the Linux package alternatives '${request}' exists in the package indexes of this distribution." >&2
      exit 1
    fi
    echo "The cached package indexes know none of '${request}'; refreshing once."
    refresh_package_indexes
  done
}

missing_packages=()
for package in "$@"; do
  if [[ "${package}" == *'|'* ]]; then
    select_alternative "${package}"
    if [[ -n "${selected_alternative}" ]]; then
      missing_packages+=("${selected_alternative}")
    fi
  elif ! package_is_installed "${package}"; then
    missing_packages+=("${package}")
  fi
done

if (( ${#missing_packages[@]} == 0 )); then
  echo "All requested Linux packages are already installed; skipping apt."
  exit 0
fi

echo "Installing missing Linux packages: ${missing_packages[*]}"
if sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get install -y \
  -o Acquire::Retries=3 \
  -o Dpkg::Use-Pty=0 \
  "${missing_packages[@]}"; then
  exit 0
fi

if (( indexes_refreshed == 0 )); then
  echo "The cached package indexes could not satisfy the request; refreshing once."
  refresh_package_indexes
else
  echo "The refreshed package indexes could not satisfy the request; trying once more."
fi
sudo env DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a apt-get install -y \
  -o Acquire::Retries=3 \
  -o Dpkg::Use-Pty=0 \
  "${missing_packages[@]}"
