# Build context is the repository root, so Directory.Build.props,
# Directory.Packages.props, global.json and client/ are all in scope.
#
# The client is built HERE rather than copied in from a previous step. An image
# that is only correct when whoever built it remembered to run a script is the
# class of defect §4's deployment shape exists to close: for eleven phases the
# published image contained no application at all and nothing said so, because
# nothing asked the image what was in it (§13.27, register row 18).
# Debian rather than Alpine, deliberately. package-lock.json is resolved on
# glibc, and the toolchain pulls platform-specific native binaries (the bundler's
# among them); on musl those optional packages are absent and the build fails
# with "command not found", which says nothing about the cause. This stage is
# discarded — only dist/ reaches the final image — so its size buys nothing.
FROM node:22 AS client
WORKDIR /client

# NODE_ENV is set explicitly and is not a formality. React resolves through its
# development export condition when it is anything other than production, and
# the result is a bundle that runs Strict Mode's double-invocation of effects —
# an artefact no user receives (§13.26). The assertion after the build is the
# durable half: setting a variable fixes today's cause, checking the output
# fails whatever tomorrow's turns out to be.
ENV NODE_ENV=production

# Playwright is a devDependency of the client and `npm ci` installs it: the
# build needs typescript and vite from the same set. Its browser download is
# ~150 MB that this image will never run.
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

COPY client/package.json client/package-lock.json ./

# --include=dev is required, and it is required BECAUSE of NODE_ENV above: npm
# reads that variable too and omits devDependencies when it says production —
# which removes typescript and the bundler, the very tools this stage exists to
# run. The failure is `tsc: not found` and an exit code of 127, which names
# neither the variable nor the packages.
#
# One variable, set for React's export conditions, silently changing what a
# different tool installs. The two uses are unrelated and the name is shared.
RUN npm ci --include=dev

COPY client/ ./
RUN npm run build \
 && if grep -rq 'Download the React DevTools' dist/assets/; then \
        echo "FAIL: dist contains React's development build (NODE_ENV=$NODE_ENV)." >&2; \
        exit 1; \
    fi

# PINNED TO A FEATURE BAND, and this line is the fix for §13.68 rather than a
# tidy-up. It was `sdk:10.0`, which floats: on 2026-09-25 it resolved to a
# 10.0.1xx SDK and this image built; a day later the tag had moved to 10.0.401,
# a different feature band with a newer Roslyn, and the same source failed
# IDE0055 under TreatWarningsAsErrors. Nothing in the repository had changed.
#
# The format gate is pinned — actions/setup-dotnet resolves global.json — so it
# ran a different compiler from the one that builds what ships, and agreement
# between them was a coincidence of timing rather than a property. Same feature
# band on both sides is what makes `dotnet format --verify-no-changes` a
# statement about this image.
#
# global.json alone does not do it: `rollForward: latestFeature` explicitly
# permits a newer band, so copying it in constrains nothing. It is now
# `latestPatch`, which is the other half.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103 AS build
WORKDIR /source

# Restore first, on just the files that affect it, so a source-only change
# does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Crdt.Core/Crdt.Core.csproj                     src/Crdt.Core/
COPY src/Editor.Domain/Editor.Domain.csproj             src/Editor.Domain/
COPY src/Editor.Infrastructure/Editor.Infrastructure.csproj src/Editor.Infrastructure/
COPY src/Editor.Api/Editor.Api.csproj                   src/Editor.Api/
RUN dotnet restore src/Editor.Api/Editor.Api.csproj

COPY src/ src/
RUN dotnet publish src/Editor.Api/Editor.Api.csproj \
        --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY --from=client /client/dist ./wwwroot

# §9's single-page application, served from this origin. Defaulted here rather
# than left to the deployment: the files are in the image, so the path that
# finds them is a property of the image too, and a stack that forgot to set it
# would serve the API alone and look healthy (§13.28).
ENV Spa__RootPath=/app/wwwroot

# An HTTP client, for the one thing that needs one: the container healthcheck
# that polls §10's /health/ready.
#
# The cost is real and worth naming — a runtime image with curl in it hands an
# attacker with code execution an HTTP client — so this is not a convenience.
# Without it the readiness probe is polled by nothing, which is §13.41 exactly:
# an endpoint that is correct and unreachable. The compose file makes the proxy
# wait on that healthcheck, so a readiness probe that stopped working stops the
# stack from coming up rather than going quiet.
#
# Installed before USER app, so the layer is root-owned and the application
# still runs unprivileged.
RUN apt-get update     && apt-get install -y --no-install-recommends curl     && rm -rf /var/lib/apt/lists/*

# Runs as the non-root 'app' user provided by the base image.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Editor.Api.dll"]
