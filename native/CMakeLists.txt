cmake_minimum_required(VERSION 3.20)
project(ext4core C)

if(NOT MSVC)
  message(FATAL_ERROR "This build is set up for MSVC (cl.exe).")
endif()

set(E2FS ${CMAKE_CURRENT_SOURCE_DIR}/../third_party/e2fsprogs)
set(GEN ${CMAKE_CURRENT_SOURCE_DIR}/generated)
set(PORT ${CMAKE_CURRENT_SOURCE_DIR}/port)

# libext2fs sources (same set Android builds for its Windows host tools,
# minus unix_io/windows_io/test_io/undo_io/sparse_io which we replace).
set(EXT2FS_NAMES
  alloc alloc_sb alloc_stats alloc_tables atexit badblocks bb_inode bitmaps
  bitops blkmap64_ba blkmap64_rb blknum block bmap check_desc crc16 crc32c
  csum closefs dblist dblist_dir digest_encode dirblock dirhash dir_iterate
  dupfs expanddir ext_attr extent fallocate fileio freefs gen_bitmap getenv ismounted mmp
  gen_bitmap64 get_num_dirs get_pathname getsize getsectsize hashmap i_block
  icount ind_block inline inline_data inode io_manager link llseek lookup
  mkdir mkjournal namei native newdir nls_utf8 openfs orphan progress punch
  rbtree read_bb res_gdt rw_bitmaps sha256 sha512 swapfs symlink unlink
  valid_blk version)
list(TRANSFORM EXT2FS_NAMES PREPEND ${E2FS}/lib/ext2fs/)
list(TRANSFORM EXT2FS_NAMES APPEND .c)

set(ET_SOURCES
  ${E2FS}/lib/et/error_message.c
  ${E2FS}/lib/et/et_name.c
  ${E2FS}/lib/et/init_et.c
  ${E2FS}/lib/et/com_err.c
  ${GEN}/ext2_err.c)

# Journal replay, as used by fuse2fs.
set(JOURNAL_SOURCES
  ${E2FS}/debugfs/journal.c
  ${E2FS}/e2fsck/recovery.c
  ${E2FS}/e2fsck/revoke.c)

add_library(ext2fs_static STATIC ${EXT2FS_NAMES} ${ET_SOURCES} ${JOURNAL_SOURCES})
target_include_directories(ext2fs_static PUBLIC
  ${PORT} ${PORT}/posix ${GEN} ${E2FS}/lib ${E2FS}/lib/ext2fs ${E2FS}/lib/et)
target_compile_definitions(ext2fs_static PUBLIC HAVE_CONFIG_H)
target_compile_options(ext2fs_static PUBLIC /FIconfig.h)
target_compile_options(ext2fs_static PRIVATE /W0)
set_source_files_properties(${JOURNAL_SOURCES} PROPERTIES
  COMPILE_DEFINITIONS DEBUGFS
  INCLUDE_DIRECTORIES "${E2FS}/e2fsck;${E2FS}/debugfs")

add_library(ext4core SHARED
  src/ext4core.c
  src/winio.c
  src/scan.c
  src/parttable.c
  src/compat.c)
target_include_directories(ext4core PRIVATE include src)
target_compile_definitions(ext4core PRIVATE EXT4CORE_BUILD)
target_compile_options(ext4core PRIVATE /W3 /wd4996 /wd4244 /wd4267)
target_link_libraries(ext4core PRIVATE ext2fs_static ws2_32 bcrypt)

# Small command line tool used by the tests.
add_executable(e4cli tools/e4cli.c)
target_include_directories(e4cli PRIVATE include)
target_link_libraries(e4cli PRIVATE ext4core)

set_property(TARGET ext2fs_static ext4core e4cli PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
