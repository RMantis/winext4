/*
 * Hand-written replacement for the configure-generated ext2_types.h
 * (Windows x64 / MSVC: int = 32 bit, long = 32 bit, long long = 64 bit).
 */
#if (!defined(_LINUX_TYPES_H) && !defined(_BLKID_TYPES_H) && \
	!defined(_EXT2_TYPES_H))
#define _EXT2_TYPES_H

#include <stdint.h>

#define HAVE___U8
#define HAVE___S8
#define HAVE___U16
#define HAVE___S16
#define HAVE___U32
#define HAVE___S32
#define HAVE___U64
#define HAVE___S64

typedef uint8_t		__u8;
typedef int8_t		__s8;
typedef uint16_t	__u16;
typedef int16_t		__s16;
typedef uint32_t	__u32;
typedef int32_t		__s32;
typedef uint64_t	__u64;
typedef int64_t		__s64;

#endif /* _*_TYPES_H */

#ifndef EXT2_ENDIAN_H_
#define EXT2_ENDIAN_H_

#ifndef __bitwise
#define __bitwise
#endif
#define __force

typedef __u16	__bitwise	__le16;
typedef __u32	__bitwise	__le32;
typedef __u64	__bitwise	__le64;
typedef __u16	__bitwise	__be16;
typedef __u32	__bitwise	__be32;
typedef __u64	__bitwise	__be64;

#endif /* EXT2_ENDIAN_H_ */
