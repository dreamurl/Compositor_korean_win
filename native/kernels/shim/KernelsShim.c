// Additions that are NOT part of upstream Compositor. Upstream's eight .c files in the parent
// directory are copied verbatim so they can be refreshed from upstream without a merge; anything
// this port needs on top of them lives here.
#include <stdlib.h>
#include <stdint.h>
#include <stddef.h>

// wand_trace() hands back two malloc'd buffers. They must be released by the same CRT that
// allocated them, so the managed side calls this rather than its own free().
void kernels_free(void *pointer) { free(pointer); }

// Build stamp, so a running binary can prove which kernel build it linked against.
int kernels_abi_version(void) { return 1; }
