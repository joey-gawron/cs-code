; CsCode outline (tags format): the module, functions (named by their first clause), records and types.
(module_attribute
  name: (_) @name) @definition.module

(fun_decl
  .
  clause: (function_clause
    name: (_) @name)) @definition.function

(record_decl
  name: (_) @name) @definition.struct

(type_alias
  name: (type_name) @name) @definition.type
