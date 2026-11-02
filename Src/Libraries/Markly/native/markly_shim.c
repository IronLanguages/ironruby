/*
 * The few things markly.c does with cmark-gfm that are not plain calls of its
 * exported API: they reach into a struct (an extension node's type name), take
 * a cmark_strbuf by address (the houdini escapers), or free a buffer cmark
 * allocated.  Src/Libraries/Markly/Markly.cs calls these, and cmark-gfm's own
 * exported functions, by P/Invoke.  Compiled into libmarkly_cmark together with
 * the markly gem's copy of cmark-gfm by Util/build-markly.sh.
 *
 * Keeping the bodies here rather than in C# means that what they read is laid
 * out by the same compiler, from the same headers, as the code that wrote it.
 */

#include <stdlib.h>
#include <string.h>

#include "cmark-gfm.h"
#include "cmark-gfm-core-extensions.h"
#include "buffer.h"
#include "houdini.h"
#include "node.h"
#include "registry.h"
#include "syntax_extension.h"

#ifdef _WIN32
#define MARKLY_SHIM_EXPORT __declspec(dllexport)
#else
#define MARKLY_SHIM_EXPORT __attribute__((visibility("default")))
#endif

/* The ABI revision of this file, so the C# side can refuse a stale library. */
MARKLY_SHIM_EXPORT int markly_shim_version(void) { return 1; }

/* Init_markly's cmark_gfm_core_extensions_ensure_registered. */
MARKLY_SHIM_EXPORT void markly_shim_init(void) {
  cmark_gfm_core_extensions_ensure_registered();
}

/*
 * rb_node_get_type's default branch: the type name an extension gives its
 * own node types ("table", "strikethrough", ...), or NULL for a node that has
 * no extension.
 */
MARKLY_SHIM_EXPORT const char *markly_shim_extension_type_string(cmark_node *node) {
  if (node == NULL || node->extension == NULL ||
      node->extension->get_type_string_func == NULL)
    return NULL;
  return node->extension->get_type_string_func(node->extension, node);
}

/*
 * rb_html_escape_href / rb_html_escape_html.  Answers a NUL-terminated buffer
 * from cmark's default allocator (free it with markly_shim_free), as
 * cmark_strbuf_detach does in markly.c.
 */
MARKLY_SHIM_EXPORT char *markly_shim_escape_href(const uint8_t *src, int32_t size) {
  cmark_strbuf buf = CMARK_BUF_INIT(cmark_get_default_mem_allocator());
  houdini_escape_href(&buf, src, (bufsize_t)size);
  return (char *)cmark_strbuf_detach(&buf);
}

MARKLY_SHIM_EXPORT char *markly_shim_escape_html(const uint8_t *src, int32_t size) {
  cmark_strbuf buf = CMARK_BUF_INIT(cmark_get_default_mem_allocator());
  houdini_escape_html0(&buf, src, (bufsize_t)size, 0);
  return (char *)cmark_strbuf_detach(&buf);
}

/*
 * Free what cmark_render_* or the escapers above returned.  It has to be this
 * library's free: on Windows the C runtime the DLL was linked against need not
 * be the one .NET's NativeMemory uses.
 */
MARKLY_SHIM_EXPORT void markly_shim_free(void *ptr) {
  cmark_get_default_mem_allocator()->free(ptr);
}

/* strlen, for the C strings cmark hands back: rb_str_new2 stops at the first NUL too. */
MARKLY_SHIM_EXPORT size_t markly_shim_strlen(const char *str) {
  return strlen(str);
}

/*
 * rb_Markly_extensions: the registered syntax extensions' names, in registry
 * order, each followed by a NUL, the list ended by an empty name.  Freed with
 * markly_shim_free.
 */
MARKLY_SHIM_EXPORT char *markly_shim_extension_names(void) {
  cmark_mem *mem = cmark_get_default_mem_allocator();
  cmark_llist *exts = cmark_list_syntax_extensions(mem), *it;
  size_t total = 1;
  for (it = exts; it; it = it->next)
    total += strlen(((cmark_syntax_extension *)it->data)->name) + 1;

  char *result = (char *)mem->calloc(total, 1);
  char *p = result;
  for (it = exts; it; it = it->next) {
    const char *name = ((cmark_syntax_extension *)it->data)->name;
    size_t len = strlen(name);
    memcpy(p, name, len + 1);
    p += len + 1;
  }
  *p = '\0';

  cmark_llist_free(mem, exts);
  return result;
}

/*
 * Build a cmark_llist of extensions for cmark_render_html, as rb_render_html
 * does: NULL-terminated array in, list out (free it with
 * markly_shim_free_extension_list).
 */
MARKLY_SHIM_EXPORT cmark_llist *markly_shim_extension_list(cmark_syntax_extension **exts, int32_t count) {
  cmark_mem *mem = cmark_get_default_mem_allocator();
  cmark_llist *list = NULL;
  for (int32_t i = 0; i < count; i++)
    list = cmark_llist_append(mem, list, exts[i]);
  return list;
}

MARKLY_SHIM_EXPORT void markly_shim_free_extension_list(cmark_llist *list) {
  cmark_llist_free(cmark_get_default_mem_allocator(), list);
}
