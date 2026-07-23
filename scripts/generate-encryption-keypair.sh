#!/usr/bin/env bash
#
# DEPRECATED: Self-hosted Interfold deployments should use the Interfold.Bootstrapper binary
# (tools/Interfold.Bootstrapper) instead. The bootstrapper's SecretsPhase generates the RSA-2048
# keypair in pure C# via System.Security.Cryptography and persists it in deploy/secrets/secrets.json
# with mode 0600 alongside the other generated credentials. This script is retained for one release
# for compatibility with existing developer automation; it will be removed.
#
# Migration: `sudo ./interfold-bootstrap bootstrap --config interfold.bootstrap.json`
#
set -euo pipefail

OUTPUT_DIR="${1:-"$(dirname "$0")/keys"}"
KEY_SIZE="${KEY_SIZE:-2048}"
FORCE="${FORCE:-false}"

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl is required but not found in PATH" >&2
  exit 1
fi

if ! [[ "$KEY_SIZE" =~ ^[0-9]+$ ]] || [ "$KEY_SIZE" -lt 2048 ]; then
  echo "KEY_SIZE must be an integer >= 2048" >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"

PRIVATE_PEM="$OUTPUT_DIR/encryption-private.pem"
PUBLIC_PEM="$OUTPUT_DIR/encryption-public.pem"
PRIVATE_B64="$OUTPUT_DIR/encryption-private.base64.txt"
ENV_SNIPPET="$OUTPUT_DIR/encryption-env.txt"

if [ "$FORCE" != "true" ]; then
  for f in "$PRIVATE_PEM" "$PUBLIC_PEM" "$PRIVATE_B64" "$ENV_SNIPPET"; do
    if [ -f "$f" ]; then
      echo "Output file already exists: $f" >&2
      echo "Set FORCE=true to overwrite." >&2
      exit 1
    fi
  done
fi

openssl genpkey -algorithm RSA -pkeyopt "rsa_keygen_bits:${KEY_SIZE}" -out "$PRIVATE_PEM"
openssl rsa -in "$PRIVATE_PEM" -pubout -out "$PUBLIC_PEM"

BASE64_VALUE="$(base64 < "$PRIVATE_PEM" | tr -d '\n')"
printf "%s" "$BASE64_VALUE" > "$PRIVATE_B64"

cat > "$ENV_SNIPPET" <<EOF
# Set this in your secret store or environment
ENCRYPTION_PRIVATE_KEY=${BASE64_VALUE}
EOF

echo "Generated encryption keypair:"
echo "  Private PEM:  $PRIVATE_PEM"
echo "  Public PEM:   $PUBLIC_PEM"
echo "  Private b64:  $PRIVATE_B64"
echo "  Env snippet:  $ENV_SNIPPET"
echo
echo "Use the base64 value for ENCRYPTION_PRIVATE_KEY."
