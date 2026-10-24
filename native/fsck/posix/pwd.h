/* Minimal <pwd.h> for MSVC builds of e2fsck: no user database. */
#ifndef WINEXT4_PWD_H
#define WINEXT4_PWD_H

struct passwd {
	char *pw_name;
	char *pw_dir;
};

static __inline struct passwd *getpwuid(int uid)
{
	(void) uid;
	return 0;
}

#endif
