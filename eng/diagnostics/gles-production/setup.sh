#!/bin/sh
set -eu
export DEBIAN_FRONTEND=noninteractive
apt-get update
mkdir /evidence/verified-debs
cd /evidence/verified-debs
apt-get download libegl-mesa0=26.0.8-1ubuntu0.3 libgbm1=26.0.8-1ubuntu0.3 libgl1-mesa-dri=26.0.8-1ubuntu0.3 libglx-mesa0=26.0.8-1ubuntu0.3 mesa-libgallium=26.0.8-1ubuntu0.3
sha256sum -c /inputs/$RID.sha256
apt-get install -y --no-install-recommends ./*.deb python3 xvfb xauth libx11-6 libegl1 libgles2
