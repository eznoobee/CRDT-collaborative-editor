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

# DNS:editor-oidc is here because deploy/docker-compose.dev-oidc.yml serves the
# local development issuer with this same certificate, under that name, and an
# OIDC issuer URL has to resolve to the same service from the browser and from
# inside the API's container. One certificate means one trust decision for the
# developer instead of a new one on every `docker compose up`; leaving the name
# out does not fail here, it fails later as a handshake the API cannot verify.
#
# Harmless when the issuer overlay is not used: a subjectAltName nothing
# resolves is a name nothing asks for.
sans='subjectAltName=DNS:localhost,IP:127.0.0.1,DNS:editor-oidc'
[[ -n "$extra" ]] && sans="$sans,$extra"

# NO `2>/dev/null`, AND THAT IS THE WHOLE POINT OF THIS BLOCK. It was there,
# and it hid the failure that cost three rounds of debugging: openssl wrote the
# key, failed before writing the certificate, and `set -e` aborted the script
# before the chmod and the summary below — silently, because the reason had been
# sent to /dev/null. What surfaced instead was Docker inventing a directory at
# the missing path and Node dying with EISDIR two layers away (§13.70).
#
# MSYS_NO_PATHCONV / MSYS2_ARG_CONV_EXCL are for Git Bash on Windows, which
# rewrites arguments that look like POSIX paths into Windows ones: `-subj
# '/CN=localhost'` arrives as `-subj 'C:/Program Files/Git/CN=localhost'` and
# openssl rejects it. Both variables are ignored everywhere else, so this is not
# a platform branch — it is the same command, told not to be helpful.
if ! MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
    openssl req -x509 -newkey rsa:2048 -sha256 -days 30 -nodes \
        -keyout "$out/key.pem" -out "$out/cert.pem" \
        -subj '/CN=localhost' \
        -addext "$sans"; then
    echo >&2
    echo "FAILED: openssl exited non-zero; its error is above." >&2
    echo "  Nothing was written that can be relied on. $out may hold a" >&2
    echo "  half-written key from the attempt; delete it before retrying." >&2
    exit 1
fi

# WHAT THE SCRIPT PRODUCED, not that it reached the end (§13.15). The previous
# version asserted neither: it printed "Wrote ..." because control arrived at
# the echo, which is true of a run that wrote one file of two.
problems=''
[[ -s "$out/cert.pem" ]] || problems+="  missing or empty: $out/cert.pem"$'\n'
[[ -s "$out/key.pem" ]]  || problems+="  missing or empty: $out/key.pem"$'\n'

if [[ -z "$problems" ]]; then
    # Parseable, not merely present: a truncated PEM is a file of the right
    # name that nothing can load, and the next thing to find out would be a TLS
    # handshake failure inside a container.
    openssl x509 -in "$out/cert.pem" -noout -subject >/dev/null 2>&1 \
        || problems+="  not a readable certificate: $out/cert.pem"$'\n'
    openssl pkey -in "$out/key.pem" -noout >/dev/null 2>&1 \
        || problems+="  not a readable private key: $out/key.pem"$'\n'
fi

# And the name the dev issuer is reached by. `-addext` failing quietly would
# leave a certificate that works for localhost and fails for editor-oidc, which
# surfaces as an unverifiable issuer long after this script has said it was fine.
if [[ -z "$problems" ]] \
    && ! openssl x509 -in "$out/cert.pem" -noout -ext subjectAltName 2>/dev/null \
        | grep -q 'DNS:editor-oidc'; then
    problems+="  certificate has no DNS:editor-oidc name; -addext did not take"$'\n'
fi

if [[ -n "$problems" ]]; then
    echo >&2
    echo "FAILED: openssl reported success and the output is not usable." >&2
    printf '%s' "$problems" >&2
    echo "  Delete $out and retry; if it repeats, run the openssl command" >&2
    echo "  above by hand — its stderr is no longer suppressed." >&2
    exit 1
fi

chmod 600 "$out/key.pem"

echo "Wrote $out/cert.pem and $out/key.pem (self-signed, 30 days, localhost)."
echo
echo "Add to .env:"
echo "  TLS_CERT_FILE=./$out/cert.pem"
echo "  TLS_KEY_FILE=./$out/key.pem"
