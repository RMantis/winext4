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

add_library(ext2fs_static STATIC ${EXT2FS_NAMES} ${ET_SOURCES})
target_include_directories(ext2fs_static PUBLIC
  ${PORT} ${PORT}/posix ${GEN} ${E2FS}/lib ${E2FS}/lib/ext2fs ${E2FS}/lib/et)
target_compile_definitions(ext2fs_static PUBLIC HAVE_CONFIG_H)
target_compile_options(ext2fs_static PUBLIC /FIconfig.h)
target_compile_options(ext2fs_static PRIVATE /W0)

# Kept apart because e4fsck brings e2fsck's own journal code.
add_library(ext2fs_journal STATIC ${JOURNAL_SOURCES})
target_link_libraries(ext2fs_journal PUBLIC ext2fs_static)
target_compile_options(ext2fs_journal PRIVATE /W0)
target_compile_definitions(ext2fs_journal PRIVATE DEBUGFS)
target_include_directories(ext2fs_journal PRIVATE ${E2FS}/e2fsck ${E2FS}/debugfs)

add_library(ext4core SHARED
  src/ext4core.c
  src/winio.c
  src/scan.c
  src/parttable.c
  src/raw.c
  src/super.c
  src/maintenance.c
  src/overlay.c
  src/compat.c)
target_include_directories(ext4core PRIVATE include src)
target_compile_definitions(ext4core PRIVATE EXT4CORE_BUILD)
target_compile_options(ext4core PRIVATE /W3 /wd4996 /wd4244 /wd4267)
target_link_libraries(ext4core PRIVATE ext2fs_journal ext2fs_static ws2_32 bcrypt)

# Small command line tool used by the tests.
add_executable(e4cli tools/e4cli.c)
target_include_directories(e4cli PRIVATE include)
target_link_libraries(e4cli PRIVATE ext4core)

# e2fsck on WinExt4's I/O layer, run by the GUI as a separate process.
set(E2FSCK_NAMES
  unix e2fsck super pass1 pass1b pass2 pass3 pass4 pass5 journal badblocks
  util dirinfo dx_dirinfo ehandler problem message quota recovery region
  revoke ea_refcount rehash logfile readahead extents encrypted_files)
list(TRANSFORM E2FSCK_NAMES PREPEND ${E2FS}/e2fsck/)
list(TRANSFORM E2FSCK_NAMES APPEND .c)
set(SUPPORT_NAMES
  profile prof_err dict mkquota quotaio quotaio_tree quotaio_v2 parse_qtype
  cstring devname argv_parse profile_helpers)
list(TRANSFORM SUPPORT_NAMES PREPEND ${E2FS}/lib/support/)
list(TRANSFORM SUPPORT_NAMES APPEND .c)
list(REMOVE_ITEM SUPPORT_NAMES ${E2FS}/lib/support/prof_err.c)
list(APPEND SUPPORT_NAMES ${GEN}/support/prof_err.c)
set(E2P_NAMES
  feature hashstr mntopts ostype parse_num pe percent pf ps uuid encoding
  crypto_mode ljs iod errcode)
list(TRANSFORM E2P_NAMES PREPEND ${E2FS}/lib/e2p/)
list(TRANSFORM E2P_NAMES APPEND .c)

add_executable(e4fsck fsck/e4fsck.c fsck/posix_stubs.c src/winio.c src/overlay.c src/compat.c
  ${E2FS}/lib/ext2fs/initialize.c ${E2FS}/lib/ext2fs/read_bb_file.c ${E2FS}/lib/ext2fs/flushb.c
  ${E2FSCK_NAMES} ${SUPPORT_NAMES} ${E2P_NAMES})
target_include_directories(e4fsck PRIVATE
  fsck fsck/posix include src ${GEN}/support ${E2FS}/e2fsck ${E2FS}/lib/support ${E2FS})
target_compile_options(e4fsck PRIVATE /W0 /FIfsck_port.h)
target_compile_definitions(e4fsck PRIVATE E4_STATIC)
set_source_files_properties(${E2FS}/e2fsck/unix.c PROPERTIES COMPILE_DEFINITIONS main=e2fsck_main)
target_link_libraries(e4fsck PRIVATE ext2fs_static ws2_32 bcrypt)

set_property(TARGET ext2fs_static ext2fs_journal ext4core e4cli e4fsck PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
