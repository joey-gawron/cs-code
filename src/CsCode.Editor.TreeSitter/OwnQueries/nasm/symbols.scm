; CsCode outline (tags format): labels, structures and macros.
(label
  name: (word) @name) @definition.label

(struc_declaration
  name: (word) @name) @definition.struct

(preproc_multiline_macro
  name: (word) @name) @definition.macro

(preproc_def
  name: (word) @name) @definition.constant
