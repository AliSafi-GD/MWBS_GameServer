#!/bin/sh
# Loads the images of this bundle into Docker. Needs no internet.
set -e
cd "$(dirname "$0")"
docker load -i images.tar.gz
docker images | grep -E "gameserver|postgres"
