#!/bin/bash
set -euo pipefail

# Ensures the host AIO limit (fs.aio-max-nr) is sufficient for the configured workload.
#
# Scylla / Seastar refuses to start unless fs.aio-max-nr is high enough to satisfy *every*
# node's reactor reservation. The minimum and recommended values come from Seastar's own
# startup error message ("Set /proc/sys/fs/aio-max-nr to at least 66563 (minimum) or 116562
# (recommended for networking performance)"), per Scylla node. The required total is therefore
# proportional to how many Scylla nodes will run on the host concurrently — that's why the
# previous static defaults (270667 / 320666, sized for ~4 nodes) silently broke the multi-DC
# integration test fixture (7 nodes need ~870K, not 320K).
#
# Usage:
#   ensure-host-aio.sh                          # default: size for 4 Scylla nodes (legacy)
#   ensure-host-aio.sh --scylla-nodes 7         # multi-DC test fixture path
#   AIO_SCYLLA_NODES=7 ensure-host-aio.sh       # same, via env var (Docker exec friendly)
#   AIO_MIN_REQUIRED=600000 AIO_TARGET=900000 \
#     ensure-host-aio.sh                        # explicit override for advanced operators
#
# Behaviour:
#   - Reads /proc/sys/fs/aio-max-nr.
#   - If `current >= required`, exits 0 (never lowers the limit; the existing value might be
#     deliberately high for unrelated workloads).
#   - Otherwise raises it to `target`. The standard error message points at
#     `sysctl -w fs.aio-max-nr=$target` if the write fails (e.g. unprivileged container).

# --- Per-Scylla-node Seastar AIO budget (from Seastar's own startup message). Keep these in
#     sync with the constants used by Interfold.Bootstrapper/Phases/PrerequisitesPhase.cs and
#     tests/Interfold.IntegrationTests/TestServices/HostAioPrerequisite.cs. ---
PER_NODE_MIN=66563
PER_NODE_RECOMMENDED=116562
HEADROOM=50000

# --- Argument parsing (long options only; this script is invoked from CI and from C#, never
#     from a hand-typed shell, so no need for short forms). ---
SCYLLA_NODES="${AIO_SCYLLA_NODES:-4}"
while [ $# -gt 0 ]; do
  case "$1" in
    --scylla-nodes)
      SCYLLA_NODES="$2"
      shift 2
      ;;
    --scylla-nodes=*)
      SCYLLA_NODES="${1#--scylla-nodes=}"
      shift
      ;;
    -h|--help)
      sed -n '4,30p' "$0"
      exit 0
      ;;
    *)
      echo "ensure-host-aio.sh: unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if ! [[ "$SCYLLA_NODES" =~ ^[0-9]+$ ]]; then
  echo "ensure-host-aio.sh: --scylla-nodes must be a non-negative integer (got '$SCYLLA_NODES')." >&2
  exit 2
fi

# `AIO_MIN_REQUIRED` / `AIO_TARGET` overrides take precedence so an operator with an unusual
# topology (e.g. mixing Scylla with another AIO-heavy workload on the same host) can pin
# explicit values without having to model their full graph in --scylla-nodes.
COMPUTED_MIN=$(( SCYLLA_NODES * PER_NODE_MIN + HEADROOM ))
COMPUTED_TARGET=$(( SCYLLA_NODES * PER_NODE_RECOMMENDED + HEADROOM ))
MIN_REQUIRED="${AIO_MIN_REQUIRED:-$COMPUTED_MIN}"
TARGET="${AIO_TARGET:-$COMPUTED_TARGET}"

AIO_SYSCTL_PATH="/proc/sys/fs/aio-max-nr"

if [ ! -r "$AIO_SYSCTL_PATH" ]; then
  echo "ERROR: Cannot read $AIO_SYSCTL_PATH"
  exit 1
fi

current="$(cat "$AIO_SYSCTL_PATH")"
echo "ensure-host-aio: scylla-nodes=$SCYLLA_NODES min=$MIN_REQUIRED target=$TARGET current=$current"

if [ "$current" -ge "$MIN_REQUIRED" ]; then
  echo "Host AIO limit already satisfies minimum ($MIN_REQUIRED for $SCYLLA_NODES Scylla node(s))."
  exit 0
fi

echo "Host AIO limit ($current) is below minimum ($MIN_REQUIRED for $SCYLLA_NODES Scylla node(s)). Raising to $TARGET..."
if echo "$TARGET" > "$AIO_SYSCTL_PATH"; then
  updated="$(cat "$AIO_SYSCTL_PATH")"
  echo "Updated fs.aio-max-nr: $updated"
else
  echo "ERROR: Failed to update fs.aio-max-nr."
  echo "Set it manually on the host and retry:"
  echo "  sudo sysctl -w fs.aio-max-nr=$TARGET"
  exit 1
fi

if [ "$updated" -lt "$MIN_REQUIRED" ]; then
  echo "ERROR: fs.aio-max-nr is still below minimum requirement after update."
  exit 1
fi

echo "Host AIO tuning complete."
