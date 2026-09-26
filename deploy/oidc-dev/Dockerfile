# The harness issuer as a service, for a LOCAL DEVELOPMENT stack only.
# deploy/docker-compose.dev-oidc.yml says what that boundary means.
#
# Debian rather than Alpine, for the reason src/Editor.Api/Dockerfile records:
# this repository's Node toolchain is resolved on glibc, and a musl base fails
# in ways that do not name their cause. Nothing here needs that toolchain, but
# matching the base the rest of the repository uses costs nothing and removes a
# difference nobody would think to check.
FROM node:22

# NO `npm ci`, AND THAT IS NOT AN OMISSION. `client/src/interop/harness.ts`
# imports nothing but `node:*` builtins — the issuer is a `node:https` server, a
# `node:crypto` signer and about four hundred lines of OIDC — so there is no
# dependency tree to install and no lockfile to resolve. A build step that
# installed the client's dependencies would take a minute to produce nothing
# this image uses, and would couple a dev issuer to the front-end's packages.
#
# openssl is needed and is already in this base: the harness shells out to it to
# compute the certificate's SPKI pin.

WORKDIR /srv

# Run as the image's unprivileged user. The issuer binds 9443, which needs no
# privilege, and writes only the CA bundle to a volume the compose file makes
# writable.
COPY --chown=node:node client/src/interop/harness.ts ./client/src/interop/harness.ts
COPY --chown=node:node deploy/oidc-dev/server.ts ./deploy/oidc-dev/server.ts

# THE CA BUNDLE'S DIRECTORY, CREATED HERE AND OWNED BY node. This one line is
# what makes the named volume writable, and the mechanism is not obvious:
# Docker initialises an EMPTY named volume from whatever is at the mount point
# in the image, ownership included. With no /run/oidc-ca in the image there is
# nothing to copy, so Docker creates the mount point itself — root:root, mode
# 755 — and this container runs as `node`. The process then reads its
# certificate successfully and dies with EACCES writing the bundle (§13.71).
#
# ONLY ON A FRESH VOLUME. An `oidc-ca` volume that already exists keeps the
# ownership it was created with, so an existing stack needs
# `docker compose ... down -v` once. The entrypoint says so when the write
# fails, rather than leaving that to be rediscovered.
RUN mkdir -p /run/oidc-ca && chown node:node /run/oidc-ca

USER node

# TypeScript is run directly, by Node's own type stripping — no transpiler and
# no build stage. That works here because tsconfig.json already sets
# `erasableSyntaxOnly` and `verbatimModuleSyntax` for the whole client, so every
# file in it is already free of the syntax stripping cannot handle. Node 22.18+
# has it on by default; the pinned base is well past that.
ENTRYPOINT ["node", "deploy/oidc-dev/server.ts"]
