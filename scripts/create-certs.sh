#!/usr/bin/env bash
#
# DEPRECATED: Self-hosted Interfold deployments should use the Interfold.Bootstrapper binary
# (host/Interfold.Bootstrapper) instead. The bootstrapper's CertificatePhase generates the
# root CA and leaf cert in pure C# via System.Security.Cryptography and installs them into the
# system trust store. This script is retained for one release for compatibility with existing
# automation; it will be removed.
#
# Migration: `sudo ./interfold-bootstrap bootstrap --config interfold.bootstrap.json`
#
set -e

# Example usage
# ./create-certs.sh \
#   "$ROOT_NAME" \
#   "$DOMAINS" \
#   "$OUTPUT_PATH" \
#   "$PASSWORD" \
#   "$YEARS"

# ==== CONFIGURATION ====

ROOT_NAME="$1"
DOMAINS="$2"
OUTPUT_PATH="$3"
PASSWORD="$4"
YEARS="$5"

DOMAINS_ARRAY=(${DOMAINS//,/ })

INSTALL_TRUST_STORE=true   # set false to skip

# ==== Internal filenames ====

CER_OUTPUT_FILE="${ROOT_NAME}.cer"
PFX_OUTPUT_FILE="${ROOT_NAME}.pfx"
ROOT_KEY="${OUTPUT_PATH}/rootCA.key"
ROOT_CRT="${OUTPUT_PATH}/rootCA.crt"
LEAF_KEY="${OUTPUT_PATH}/leaf.key"
LEAF_CSR="${OUTPUT_PATH}/leaf.csr"
LEAF_CRT="${OUTPUT_PATH}/leaf.crt"

echo "=== Generating Root CA ==="

openssl req -x509 -newkey rsa:2048 -days $((YEARS * 365)) \
  -keyout "$ROOT_KEY" \
  -out "$ROOT_CRT" \
  -nodes \
  -subj "/CN=${ROOT_NAME}" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign"

echo "Root CA created."

# ==== Build SAN list ====
SAN=""
for domain in "${DOMAINS_ARRAY[@]}"; do
    SAN+="DNS:${domain},"
done
SAN="${SAN::-1}"


echo "=== Generating Leaf TLS Certificate ==="

openssl req -newkey rsa:2048 -nodes \
  -keyout "$LEAF_KEY" \
  -out "$LEAF_CSR" \
  -subj "/CN=${DOMAINS_ARRAY[0]}"

openssl x509 -req \
  -in "$LEAF_CSR" \
  -CA "$ROOT_CRT" \
  -CAkey "$ROOT_KEY" \
  -CAcreateserial \
  -out "$LEAF_CRT" \
  -days $((YEARS * 365)) \
  -extfile <(printf "subjectAltName=%s\nkeyUsage=digitalSignature,keyEncipherment" "$SAN")

echo "Leaf certificate created."


# ==== Export ====
cp "$ROOT_CRT" "$OUTPUT_PATH/$CER_OUTPUT_FILE"

openssl pkcs12 -export \
  -inkey "$LEAF_KEY" \
  -in "$LEAF_CRT" \
  -certfile "$ROOT_CRT" \
  -out "$OUTPUT_PATH/$PFX_OUTPUT_FILE" \
  -password pass:"$PASSWORD"

echo "Certificates exported:"
echo " - $CER_OUTPUT_FILE"
echo " - $PFX_OUTPUT_FILE"


# ==== Install into Linux system trust store ====
if [ "$INSTALL_TRUST_STORE" = true ]; then
    echo "=== Installing Root CA into System Trust Store ==="

    if [[ -d "/usr/local/share/ca-certificates" ]]; then
        # Debian / Ubuntu
        cp "$ROOT_CRT" /usr/local/share/ca-certificates/interfold-root-ca.crt
        update-ca-certificates
        echo "Installed into Debian/Ubuntu trust store."
    elif [[ -d "/etc/pki/ca-trust/source/anchors" ]]; then
        # RHEL / Fedora / CentOS
        cp "$ROOT_CRT" /etc/pki/ca-trust/source/anchors/interfold-root-ca.crt
        update-ca-trust extract
        echo "Installed into RHEL/Fedora trust store."
    else
        echo "Unknown distro — skipping trust store install."
    fi
fi

echo "Done!"