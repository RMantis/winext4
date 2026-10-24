/*
 * Minimal libuuid replacement: libext2fs/journal code only needs to test
 * and print UUIDs.  Implemented in src/compat.c.
 */
#ifndef WINEXT4_UUID_H
#define WINEXT4_UUID_H

typedef unsigned char uuid_t[16];

#define UUID_STR_SIZE 37

int uuid_is_null(const uuid_t uu);
void uuid_unparse(const uuid_t uu, char *out);
int uuid_compare(const uuid_t uu1, const uuid_t uu2);
void uuid_generate(uuid_t out);
void uuid_clear(uuid_t uu);
void uuid_copy(uuid_t dst, const uuid_t src);

#endif
