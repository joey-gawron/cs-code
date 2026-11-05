// Flattens libvterm's bitfield structs (VTermScreenCell, VTermStringFragment),
// which translate-c turns into opaque types, into plain ones that
// src/model/terminal/vterm.zig mirrors as `extern struct`s.

#include <string.h>
#include <vterm.h>

enum {
  ZT_BOLD = 1 << 0,
  ZT_ITALIC = 1 << 1,
  ZT_UNDERLINE_SHIFT = 2, // two bits: off, single, double, curly
  ZT_BLINK = 1 << 4,
  ZT_REVERSE = 1 << 5,
  ZT_CONCEAL = 1 << 6,
  ZT_STRIKE = 1 << 7,
};

typedef struct {
  uint32_t chars[VTERM_MAX_CHARS_PER_CELL];
  int32_t width;
  uint32_t flags;
  VTermColor fg, bg;
} ZtCell;

static void flatten(const VTermScreenCell *in, ZtCell *out) {
  memcpy(out->chars, in->chars, sizeof out->chars);
  out->width = in->width;
  uint32_t f = 0;
  if (in->attrs.bold) f |= ZT_BOLD;
  if (in->attrs.italic) f |= ZT_ITALIC;
  f |= (uint32_t)(in->attrs.underline & 3) << ZT_UNDERLINE_SHIFT;
  if (in->attrs.blink) f |= ZT_BLINK;
  if (in->attrs.reverse) f |= ZT_REVERSE;
  if (in->attrs.conceal) f |= ZT_CONCEAL;
  if (in->attrs.strike) f |= ZT_STRIKE;
  out->flags = f;
  out->fg = in->fg;
  out->bg = in->bg;
}

static void unflatten(const ZtCell *in, VTermScreenCell *out) {
  memset(out, 0, sizeof *out);
  memcpy(out->chars, in->chars, sizeof out->chars);
  out->width = (char)in->width;
  out->attrs.bold = !!(in->flags & ZT_BOLD);
  out->attrs.italic = !!(in->flags & ZT_ITALIC);
  out->attrs.underline = (in->flags >> ZT_UNDERLINE_SHIFT) & 3;
  out->attrs.blink = !!(in->flags & ZT_BLINK);
  out->attrs.reverse = !!(in->flags & ZT_REVERSE);
  out->attrs.conceal = !!(in->flags & ZT_CONCEAL);
  out->attrs.strike = !!(in->flags & ZT_STRIKE);
  out->fg = in->fg;
  out->bg = in->bg;
}

int zt_screen_get_cell(const VTermScreen *screen, int row, int col, ZtCell *out) {
  VTermScreenCell cell;
  VTermPos pos = {.row = row, .col = col};
  if (!vterm_screen_get_cell(screen, pos, &cell)) return 0;
  flatten(&cell, out);
  return 1;
}

// The cell arrays passed to sb_pushline / sb_popline.
void zt_cells_get(const VTermScreenCell *cells, int i, ZtCell *out) { flatten(&cells[i], out); }
void zt_cells_set(VTermScreenCell *cells, int i, const ZtCell *in) { unflatten(in, &cells[i]); }

// VTermValue: `boolean` and `number` share the same int.
int zt_value_int(const VTermValue *val) { return val->number; }

void zt_value_string(const VTermValue *val, const char **str, size_t *len, int *initial, int *final) {
  *str = val->string.str;
  *len = val->string.len;
  *initial = val->string.initial;
  *final = val->string.final;
}
