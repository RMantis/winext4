/* Minimal POSIX getopt for MSVC builds of e2fsck. */
#ifndef WINEXT4_GETOPT_H
#define WINEXT4_GETOPT_H

extern char *optarg;
extern int optind, opterr, optopt;

int getopt(int argc, char *const argv[], const char *optstring);

#endif
