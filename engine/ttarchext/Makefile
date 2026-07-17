# Builds a native ttarchext for the host platform (Linux/macOS).
#
# The native build supports every archive format except Oodle-compressed
# containers (lowercase zCTT/eCTT), because Oodle decompression works by
# loading the game's own oo2core DLL — a Windows-only trick. zlib-compressed,
# encrypted, and uncompressed archives all work.
#
# Requires zlib headers (Debian/Ubuntu: apt install zlib1g-dev).

CC     ?= cc
CFLAGS ?= -O2

ttarchext: ttarchext.c blowfish_ttarch.c blowfish_ttarch.h bf_tab.h
	$(CC) $(CFLAGS) -o $@ ttarchext.c blowfish_ttarch.c -lz

clean:
	rm -f ttarchext

.PHONY: clean
