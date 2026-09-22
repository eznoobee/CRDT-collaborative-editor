#!/usr/bin/env bash
# A TLS certificate for a local stack (PROJECT_SPEC.md §4).
#
# The Compose file requires TLS_CERT_FILE and TLS_KEY_FILE and supplies no
# default, exactly as it does for the database password and the OIDC issuer: a
# stack that comes up with a certificate somebody happened to leave lying around
# is the fallback §7 forbids. This script makes one for local use and is not a
# way to get one for a deployment.
set -euo pipefail

cd "$(dirname "$0")/.."
out="${1:-deploy/tls}"

# Extra subjectAltName entries, e.g. "IP:10.1.0.4". The walk needs one: the
# stack is reached by the host's routable address, because a container cannot
# route to the host's loopback, and a certificate that does not name the address
# in the URL fails verification no matter how trusted its issuer is.
extra="${2:-}"
mkdir -p "$out"

sans='subjectAltName=DNS:localhost,IP:127.0.0.1'
[[ -n "$extra" ]] && sans="$sans,$extra"

openssl req -x509 -newkey rsa:2048 -sha256 -days 30 -nodes \
    -keyout "$out/key.pem" -out "$out/cert.pem" \
    -subj '/CN=localhost' \
    -addext "$sans" 2>/dev/null

chmod 600 "$out/key.pem"

echo "Wrote $out/cert.pem and $out/key.pem (self-signed, 30 days, localhost)."
echo
echo "Add to .env:"
echo "  TLS_CERT_FILE=./$out/cert.pem"
echo "  TLS_KEY_FILE=./$out/key.pem"
