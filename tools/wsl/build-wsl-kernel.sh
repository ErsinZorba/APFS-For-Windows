#!/bin/bash
# WSL çekirdek kaynağını indirip modül derlemeye hazırlar (Module.symvers dahil). Yalnızca geliştirme.
set -euo pipefail
V=$(uname -r | sed 's/-microsoft-standard-WSL2//')
cd /opt
apt-get install -y -qq flex bison libssl-dev libelf-dev bc dwarves cpio python3 pahole >/dev/null 2>&1 || apt-get install -y -qq flex bison libssl-dev libelf-dev bc dwarves cpio python3 >/dev/null
if [ ! -d wsl-kernel ]; then
  curl -sSL -o k.tar.gz "https://github.com/microsoft/WSL2-Linux-Kernel/archive/refs/tags/linux-msft-wsl-$V.tar.gz"
  mkdir wsl-kernel && tar -xzf k.tar.gz -C wsl-kernel --strip-components=1 && rm k.tar.gz
fi
cd wsl-kernel
zcat /proc/config.gz > .config
make olddefconfig >/dev/null
make -j"$(nproc)" LOCALVERSION= >/opt/kbuild.log 2>&1
ls -la Module.symvers
echo KERNEL_BUILD_OK
