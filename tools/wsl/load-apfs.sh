#!/bin/bash
# linux-apfs-rw modülünü yükler (WSL VM her yeniden başladığında gerekir; modül tools/wsl/build-wsl-kernel.sh ile derlenen
# çekirdek kaynağına karşı derlenir, derleyici farkı yüzünden insmod -f gerekir).
lsmod | grep -q '^apfs ' || insmod -f /opt/linux-apfs-rw/apfs.ko 2>/dev/null || { echo "apfs.ko yüklenemedi"; exit 1; }
