/*
 * rbs_ironruby.c - the entry points IronRuby calls in librbs.
 *
 * librbs is the rbs gem's own parser - its src/ and include/, which do not depend on
 * the Ruby C API - built with this file by Util/build-librbs.sh.  What the gem's
 * ext/rbs_extension does with the Ruby C API this file does with a byte stream:
 *
 *   main.c            -> rbs_ir_parse / rbs_ir_lex below, statement for statement:
 *                        the same parser calls, the same EOF and require_eof checks,
 *                        the same type-variable table
 *   ast_translation.c -> rbs_ir_write_node and the generated rbs_ironruby_nodes.inc:
 *                        every value the C extension would hand to Ruby, tagged, in the
 *                        order it would build them
 *
 * Src/Libraries/Rbs/RbsDeserializer.cs turns the stream into the Ruby objects
 * (RBS::Types::ClassInstance.new(**h), RBS::Location, Symbols, ...), so the objects
 * themselves - and every call into Ruby - are made on the C# side.
 *
 * Stream format (little-endian):
 *
 *   value := NIL | TRUE | FALSE
 *          | NODE u8:type value*          fields in RbsNodes.Generated.cs order
 *          | ARRAY u32:n value{n}
 *          | HASH u32:n (value value){n}
 *          | EMPTY_HASH | EMPTY_ARRAY     the C extension's shared frozen objects
 *          | STRING u32:n bytes           in the buffer's encoding
 *          | STRING_UTF8 u32:n bytes
 *          | INTEGER u32:n bytes          its source text; Ruby's String#to_i reads it
 *          | SYMBOL u32:n bytes           in the buffer's encoding
 *          | KEYWORD u32:n bytes          ASCII
 *          | LOCATION i32:start i32:end u16:capacity
 *                     [u16:n u32:required_bitmap (u32:len name i32:start i32:end){n}]
 *                                     the bracketed part only when capacity > 0
 *
 *   error := u8:syntax_error i32:start i32:end u32:n token_type u32:n message
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "rbs/parser.h"
#include "rbs/lexer.h"
#include "rbs/util/rbs_allocator.h"
#include "rbs/util/rbs_constant_pool.h"
#include "rbs/util/rbs_encoding.h"

#if defined(_WIN32)
#define RBS_IR_EXPORT __declspec(dllexport)
#else
#define RBS_IR_EXPORT __attribute__((visibility("default")))
#endif

/* Bumped whenever the stream format or an entry point changes; RbsNative.cs checks it. */
#define RBS_IR_ABI_VERSION 1

enum {
    RBS_IR_NIL = 0,
    RBS_IR_TRUE = 1,
    RBS_IR_FALSE = 2,
    RBS_IR_NODE = 3,
    RBS_IR_ARRAY = 4,
    RBS_IR_HASH = 5,
    RBS_IR_EMPTY_HASH = 6,
    RBS_IR_EMPTY_ARRAY = 7,
    RBS_IR_STRING = 8,
    RBS_IR_STRING_UTF8 = 9,
    RBS_IR_INTEGER = 10,
    RBS_IR_SYMBOL = 11,
    RBS_IR_KEYWORD = 12,
    RBS_IR_LOCATION = 13,
};

/* What rbs_ir_parse parses: one per RBS::Parser._parse_* method. */
enum {
    RBS_IR_PARSE_TYPE = 0,
    RBS_IR_PARSE_METHOD_TYPE = 1,
    RBS_IR_PARSE_SIGNATURE = 2,
    RBS_IR_PARSE_TYPE_PARAMS = 3,
    RBS_IR_PARSE_INLINE_LEADING_ANNOTATION = 4,
    RBS_IR_PARSE_INLINE_TRAILING_ANNOTATION = 5,
};

enum {
    RBS_IR_FLAG_REQUIRE_EOF = 1,
    RBS_IR_FLAG_VOID_ALLOWED = 2,
    RBS_IR_FLAG_SELF_ALLOWED = 4,
    RBS_IR_FLAG_MODULE_TYPE_PARAMS = 8,
};

/* rbs_ir_parse's answers. */
#define RBS_IR_ERROR 0             /* the result is an error */
#define RBS_IR_OK 1                /* the result is a value */
#define RBS_IR_UNKNOWN_ENCODING -1 /* rbs has no such encoding */

typedef struct {
    uint8_t *data;
    size_t length;
    size_t capacity;
    rbs_constant_pool_t *constant_pool; /* the parser's, for RBS::AST::Symbol */
} rbs_ir_writer_t;

static void rbs_ir_reserve(rbs_ir_writer_t *w, size_t n) {
    if (w->length + n <= w->capacity) return;
    size_t capacity = w->capacity == 0 ? 1024 : w->capacity;
    while (capacity < w->length + n) capacity *= 2;
    uint8_t *data = (uint8_t *) realloc(w->data, capacity);
    if (data == NULL) abort();
    w->data = data;
    w->capacity = capacity;
}

static void rbs_ir_write_u8(rbs_ir_writer_t *w, uint8_t value) {
    rbs_ir_reserve(w, 1);
    w->data[w->length++] = value;
}

static void rbs_ir_write_u32(rbs_ir_writer_t *w, uint32_t value) {
    rbs_ir_reserve(w, 4);
    for (int i = 0; i < 4; i++) w->data[w->length++] = (uint8_t) (value >> (8 * i));
}

static void rbs_ir_write_i32(rbs_ir_writer_t *w, int32_t value) {
    rbs_ir_write_u32(w, (uint32_t) value);
}

static void rbs_ir_write_u16(rbs_ir_writer_t *w, uint16_t value) {
    rbs_ir_reserve(w, 2);
    w->data[w->length++] = (uint8_t) value;
    w->data[w->length++] = (uint8_t) (value >> 8);
}

/* A count written before the items it counts: reserve it, then patch it in.  The lists'
 * length fields are not relied on - rbs_hash_set, for one, never updates its hash's. */
static size_t rbs_ir_begin_count(rbs_ir_writer_t *w) {
    size_t at = w->length;
    rbs_ir_write_u32(w, 0);
    return at;
}

static void rbs_ir_end_count(rbs_ir_writer_t *w, size_t at, uint32_t count) {
    for (int i = 0; i < 4; i++) w->data[at + i] = (uint8_t) (count >> (8 * i));
}

static void rbs_ir_write_bytes(rbs_ir_writer_t *w, const void *bytes, size_t n) {
    rbs_ir_write_u32(w, (uint32_t) n);
    rbs_ir_reserve(w, n);
    if (n > 0) memcpy(w->data + w->length, bytes, n);
    w->length += n;
}

static void rbs_ir_write_cstr(rbs_ir_writer_t *w, const char *s) {
    rbs_ir_write_bytes(w, s, strlen(s));
}

static void rbs_ir_write_bool(rbs_ir_writer_t *w, bool value) {
    rbs_ir_write_u8(w, value ? RBS_IR_TRUE : RBS_IR_FALSE);
}

static void rbs_ir_write_string(rbs_ir_writer_t *w, uint8_t tag, const rbs_string_t *s) {
    rbs_ir_write_u8(w, tag);
    rbs_ir_write_bytes(w, s->start, rbs_string_len(*s));
}

/* A name in RBS_GLOBAL_CONSTANT_POOL: a keyword, or a location child's name. */
static void rbs_ir_write_global_constant(rbs_ir_writer_t *w, rbs_constant_id_t id) {
    rbs_constant_t *constant = rbs_constant_pool_id_to_constant(RBS_GLOBAL_CONSTANT_POOL, id);
    rbs_ir_write_bytes(w, constant->start, constant->length);
}

/* rbs_loc_to_ruby_location: the range, and the children the legacy location copies. */
static void rbs_ir_write_location(rbs_ir_writer_t *w, rbs_location_t *loc) {
    if (loc == NULL) {
        rbs_ir_write_u8(w, RBS_IR_NIL);
        return;
    }

    rbs_ir_write_u8(w, RBS_IR_LOCATION);
    rbs_ir_write_i32(w, loc->rg.start.char_pos);
    rbs_ir_write_i32(w, loc->rg.end.char_pos);

    rbs_loc_children *children = loc->children;
    if (children == NULL) {
        rbs_ir_write_u16(w, 0); /* no children allocated */
        return;
    }

    rbs_ir_write_u16(w, children->cap);
    rbs_ir_write_u16(w, children->len);
    rbs_ir_write_u32(w, children->required_p);
    for (unsigned short i = 0; i < children->len; i++) {
        rbs_ir_write_global_constant(w, children->entries[i].name);
        rbs_ir_write_i32(w, children->entries[i].rg.start);
        rbs_ir_write_i32(w, children->entries[i].rg.end);
    }
}

/* rbs_location_list_to_ruby_array */
static void rbs_ir_write_location_list(rbs_ir_writer_t *w, rbs_location_list_t *list) {
    if (list == NULL) {
        rbs_ir_write_u8(w, RBS_IR_EMPTY_ARRAY);
        return;
    }

    rbs_ir_write_u8(w, RBS_IR_ARRAY);
    size_t at = rbs_ir_begin_count(w);
    uint32_t count = 0;
    for (rbs_location_list_node_t *n = list->head; n != NULL; n = n->next, count++) {
        rbs_ir_write_location(w, n->loc);
    }
    rbs_ir_end_count(w, at, count);
}

static void rbs_ir_write_node(rbs_ir_writer_t *w, rbs_node_t *instance);

/* rbs_node_list_to_ruby_array: always a new Array. */
static void rbs_ir_write_node_list(rbs_ir_writer_t *w, rbs_node_list_t *list) {
    rbs_ir_write_u8(w, RBS_IR_ARRAY);
    size_t at = rbs_ir_begin_count(w);
    uint32_t count = 0;
    for (rbs_node_list_node_t *n = list->head; n != NULL; n = n->next, count++) {
        rbs_ir_write_node(w, n->node);
    }
    rbs_ir_end_count(w, at, count);
}

/* rbs_hash_to_ruby_hash: the shared EMPTY_HASH when there is nothing in it. */
static void rbs_ir_write_hash(rbs_ir_writer_t *w, rbs_hash_t *hash) {
    if (!hash->head) {
        rbs_ir_write_u8(w, RBS_IR_EMPTY_HASH);
        return;
    }

    rbs_ir_write_u8(w, RBS_IR_HASH);
    size_t at = rbs_ir_begin_count(w);
    uint32_t count = 0;
    for (rbs_hash_node_t *n = hash->head; n != NULL; n = n->next, count++) {
        rbs_ir_write_node(w, n->key);
        rbs_ir_write_node(w, n->value);
    }
    rbs_ir_end_count(w, at, count);
}

#include "rbs_ironruby_nodes.inc"

/* rbs_struct_to_ruby_value */
static void rbs_ir_write_node(rbs_ir_writer_t *w, rbs_node_t *instance) {
    if (instance == NULL) {
        rbs_ir_write_u8(w, RBS_IR_NIL);
        return;
    }

    switch (instance->type) {
    case RBS_AST_BOOL:
        rbs_ir_write_bool(w, ((rbs_ast_bool_t *) instance)->value);
        return;
    case RBS_AST_INTEGER:
        rbs_ir_write_string(w, RBS_IR_INTEGER, &((rbs_ast_integer_t *) instance)->string_representation);
        return;
    case RBS_AST_STRING:
        rbs_ir_write_string(w, RBS_IR_STRING_UTF8, &((rbs_ast_string_t *) instance)->string);
        return;
    case RBS_SIGNATURE: {
        rbs_signature_t *signature = (rbs_signature_t *) instance;
        rbs_ir_write_u8(w, RBS_IR_ARRAY);
        rbs_ir_write_u32(w, 2);
        rbs_ir_write_node_list(w, signature->directives);
        rbs_ir_write_node_list(w, signature->declarations);
        return;
    }
    case RBS_TYPES_RECORD_FIELD_TYPE: {
        rbs_types_record_field_type_t *field = (rbs_types_record_field_type_t *) instance;
        rbs_ir_write_u8(w, RBS_IR_ARRAY);
        rbs_ir_write_u32(w, 2);
        rbs_ir_write_node(w, field->type);
        rbs_ir_write_bool(w, field->required);
        return;
    }
    case RBS_KEYWORD:
        rbs_ir_write_u8(w, RBS_IR_KEYWORD);
        rbs_ir_write_global_constant(w, ((rbs_keyword_t *) instance)->constant_id);
        return;
    case RBS_AST_SYMBOL: {
        rbs_constant_t *constant = rbs_constant_pool_id_to_constant(w->constant_pool, ((rbs_ast_symbol_t *) instance)->constant_id);
        rbs_ir_write_u8(w, RBS_IR_SYMBOL);
        rbs_ir_write_bytes(w, constant->start, constant->length);
        return;
    }
    default:
        break;
    }

    if (instance->type < 1 || instance->type > RBS_IR_NODE_TYPE_COUNT) abort();
    rbs_ir_write_u8(w, RBS_IR_NODE);
    rbs_ir_write_u8(w, (uint8_t) instance->type);
    if (!rbs_ir_write_node_fields(w, instance)) abort();
}

static int rbs_ir_finish(rbs_ir_writer_t *w, uint8_t **out, size_t *out_length, int status) {
    *out = w->data;
    *out_length = w->length;
    return status;
}

/* raise_error: what RbsDeserializer needs to raise RBS::ParsingError (or RuntimeError). */
static int rbs_ir_error(rbs_ir_writer_t *w, rbs_error_t *error, uint8_t **out, size_t *out_length) {
    w->length = 0;
    rbs_ir_write_u8(w, error->syntax_error ? 1 : 0);
    rbs_ir_write_i32(w, error->token.range.start.char_pos);
    rbs_ir_write_i32(w, error->token.range.end.char_pos);
    rbs_ir_write_cstr(w, rbs_token_type_str(error->token.type));
    rbs_ir_write_cstr(w, error->message);
    return rbs_ir_finish(w, out, out_length, RBS_IR_ERROR);
}

static const rbs_encoding_t *rbs_ir_find_encoding(const char *name) {
    return rbs_encoding_find((const uint8_t *) name, (const uint8_t *) (name + strlen(name)));
}

#ifndef RBS_IR_RBS_VERSION
#error "build with -DRBS_IR_RBS_VERSION=<the rbs gem's version> (Util/build-librbs.sh does)"
#endif

/* The rbs release src/ came from; rbs_extension.rb loads only under that RBS::VERSION. */
RBS_IR_EXPORT const char *rbs_ir_rbs_version(void) {
    return RBS_IR_RBS_VERSION;
}

/* The ABI this library speaks; RbsNative.cs refuses any other. */
RBS_IR_EXPORT int rbs_ir_abi_version(void) {
    return RBS_IR_ABI_VERSION;
}

/* Init_rbs_extension's share: the global constant pool the parser interns keywords in. */
RBS_IR_EXPORT void rbs_ir_init(void) {
    static bool initialized = false;
    if (initialized) return;
    initialized = true;

    const size_t num_uniquely_interned_strings = 26;
    rbs_constant_pool_init(RBS_GLOBAL_CONSTANT_POOL, num_uniquely_interned_strings);
}

RBS_IR_EXPORT void rbs_ir_free(uint8_t *data) {
    free(data);
}

/*
 * One RBS::Parser._parse_* call.
 *
 * `source` is the buffer's whole content, NUL-terminated as a Ruby String is (the lexer
 * finds the end of the input by its NUL); `variables` is NULL for a nil variable list, and
 * otherwise `variable_count` (u32 length, bytes) names.  The caller has done what main.c
 * does before it reaches the parser: the range and the type-variable checks.
 */
RBS_IR_EXPORT int rbs_ir_parse(int kind, const char *source, int source_length, const char *encoding_name,
                               int start_pos, int end_pos, const uint8_t *variables, int variable_count,
                               int flags, uint8_t **out, size_t *out_length) {
    rbs_ir_writer_t w = { 0 };
    *out = NULL;
    *out_length = 0;

    const rbs_encoding_t *encoding = rbs_ir_find_encoding(encoding_name);
    if (encoding == NULL) return RBS_IR_UNKNOWN_ENCODING;

    rbs_parser_t *parser = rbs_parser_new(rbs_string_new(source, source + source_length), encoding, start_pos, end_pos);
    w.constant_pool = &parser->constant_pool;

    int status = RBS_IR_OK;

    /* declare_type_variables */
    if (variables != NULL) {
        rbs_parser_push_typevar_table(parser, true);

        const uint8_t *p = variables;
        for (int i = 0; i < variable_count; i++) {
            uint32_t length = (uint32_t) p[0] | ((uint32_t) p[1] << 8) | ((uint32_t) p[2] << 16) | ((uint32_t) p[3] << 24);
            p += 4;
            rbs_constant_id_t id = rbs_constant_pool_insert_shared(&parser->constant_pool, p, length);
            p += length;

            if (!rbs_parser_insert_typevar(parser, id)) {
                status = rbs_ir_error(&w, parser->error, out, out_length);
                rbs_parser_free(parser);
                return status;
            }
        }
    }

    rbs_node_t *result = NULL;
    bool is_list = false;
    rbs_node_list_t *list = NULL;

    switch (kind) {
    case RBS_IR_PARSE_TYPE: {
        /* parse_type_try */
        if (parser->next_token.type == pEOF) break;

        rbs_node_t *type;
        rbs_parse_type(parser, &type, (flags & RBS_IR_FLAG_VOID_ALLOWED) != 0, (flags & RBS_IR_FLAG_SELF_ALLOWED) != 0);
        if (parser->error != NULL) goto error;

        if (flags & RBS_IR_FLAG_REQUIRE_EOF) {
            rbs_parser_advance(parser);
            if (parser->current_token.type != pEOF) {
                rbs_parser_set_error(parser, parser->current_token, true, "expected a token `%s`", rbs_token_type_str(pEOF));
                goto error;
            }
        }

        result = type;
        break;
    }
    case RBS_IR_PARSE_METHOD_TYPE: {
        /* parse_method_type_try */
        if (parser->next_token.type == pEOF) break;

        rbs_method_type_t *method_type = NULL;
        rbs_parse_method_type(parser, &method_type, (flags & RBS_IR_FLAG_REQUIRE_EOF) != 0);
        if (parser->error != NULL) goto error;

        result = (rbs_node_t *) method_type;
        break;
    }
    case RBS_IR_PARSE_SIGNATURE: {
        /* parse_signature_try */
        rbs_signature_t *signature = NULL;
        rbs_parse_signature(parser, &signature);
        if (parser->error != NULL) goto error;

        result = (rbs_node_t *) signature;
        break;
    }
    case RBS_IR_PARSE_TYPE_PARAMS: {
        /* parse_type_params_try */
        if (parser->next_token.type == pEOF) break;

        rbs_node_list_t *params = NULL;
        rbs_parse_type_params(parser, (flags & RBS_IR_FLAG_MODULE_TYPE_PARAMS) != 0, &params);
        if (parser->error != NULL) goto error;

        is_list = true;
        list = params;
        break;
    }
    case RBS_IR_PARSE_INLINE_LEADING_ANNOTATION:
    case RBS_IR_PARSE_INLINE_TRAILING_ANNOTATION: {
        /* parse_inline_{leading,trailing}_annotation_try */
        rbs_ast_ruby_annotations_t *annotation = NULL;
        bool success = kind == RBS_IR_PARSE_INLINE_LEADING_ANNOTATION
            ? rbs_parse_inline_leading_annotation(parser, &annotation)
            : rbs_parse_inline_trailing_annotation(parser, &annotation);
        if (parser->error != NULL) goto error;

        if (success && annotation != NULL) {
            result = (rbs_node_t *) annotation;
        }
        break;
    }
    default:
        abort();
    }

    if (is_list) {
        rbs_ir_write_node_list(&w, list);
    } else {
        rbs_ir_write_node(&w, result);
    }
    status = rbs_ir_finish(&w, out, out_length, RBS_IR_OK);
    rbs_parser_free(parser);
    return status;

error:
    status = rbs_ir_error(&w, parser->error, out, out_length);
    rbs_parser_free(parser);
    return status;
}

/*
 * RBS::Parser._lex: every token up to and including pEOF, as
 *   u32:count (u32:n type_name i32:start i32:end){count}
 */
RBS_IR_EXPORT int rbs_ir_lex(const char *source, int source_length, const char *encoding_name, int end_pos,
                             uint8_t **out, size_t *out_length) {
    rbs_ir_writer_t w = { 0 };
    *out = NULL;
    *out_length = 0;

    const rbs_encoding_t *encoding = rbs_ir_find_encoding(encoding_name);
    if (encoding == NULL) return RBS_IR_UNKNOWN_ENCODING;

    rbs_allocator_t *allocator = rbs_allocator_init();
    rbs_lexer_t *lexer = rbs_lexer_new(allocator, rbs_string_new(source, source + source_length), encoding, 0, end_pos);

    size_t at = rbs_ir_begin_count(&w);
    uint32_t count = 0;
    rbs_token_t token = NullToken;
    while (token.type != pEOF) {
        token = rbs_lexer_next_token(lexer);
        rbs_ir_write_cstr(&w, rbs_token_type_str(token.type));
        rbs_ir_write_i32(&w, token.range.start.char_pos);
        rbs_ir_write_i32(&w, token.range.end.char_pos);
        count++;
    }
    rbs_ir_end_count(&w, at, count);

    rbs_allocator_free(allocator);
    return rbs_ir_finish(&w, out, out_length, RBS_IR_OK);
}
