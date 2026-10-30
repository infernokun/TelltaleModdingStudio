# Builds libtelltale_oodle.so, which loads a game's own oo2core_N_win64.dll on
# x86-64 Linux so Oodle-compressed archives can be read there (see oodle_linux.c).
# Windows loads the DLL directly and doesn't need it.

CC     ?= cc
CFLAGS ?= -O2

libtelltale_oodle.so: oodle_linux.c
	$(CC) $(CFLAGS) -std=gnu11 -Wall -Wextra -fPIC -shared -fvisibility=hidden -o $@ oodle_linux.c -lpthread

clean:
	rm -f libtelltale_oodle.so

.PHONY: clean
