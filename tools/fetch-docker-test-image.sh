#!/usr/bin/env bash
# Pulls the one container image the Docker transport test uses (tests/CsCode.Remote.Tests/DockerTransportTests.cs), pinned by digest:
#
#   mcr.microsoft.com/dotnet/runtime-deps:10.0, OCI image index sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832
#   (looked up on mcr.microsoft.com 2026-10-29; linux/amd64 manifest sha256:32c0178ccd4906ade387fae95cb0f59612463094469c6147f550f129804d8a4b,
#   3 layers, 46,934,296 bytes compressed).
#
# Microsoft's official base image for self-contained .NET apps: a slim Debian with exactly the native libraries .NET needs (glibc, OpenSSL,
# zlib…), which is what a remote must provide for the self-contained server (debian:bookworm-slim, tried first, has no libssl: the server
# aborts there with "No usable version of libssl was found"). Pulling by digest makes docker verify the content. The image goes to docker's
# own image store. Remove it with:
#   docker image rm mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832
# (only if this script pulled it: the script says so). Nothing else (containers, networks, volumes, other images) is touched.
set -euo pipefail

ref=mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832
if docker image inspect "$ref" >/dev/null 2>&1; then
  echo "already present: $ref (not pulled by this run; leave it alone when cleaning up)"
else
  docker pull "$ref"
  echo "pulled: $ref"
fi
docker image inspect "$ref" --format 'id {{.Id}}  size {{.Size}} bytes  repo digests {{.RepoDigests}}'
