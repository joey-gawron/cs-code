; CsCode outline (tags format): functions, tests, and the containers bound to a name (const Point = struct { ... }).
(function_declaration
  name: (identifier) @name) @definition.function

(test_declaration
  [(string) (identifier)] @name) @definition.function

(variable_declaration
  (identifier) @name
  (struct_declaration)) @definition.struct

(variable_declaration
  (identifier) @name
  (enum_declaration)) @definition.enum

(variable_declaration
  (identifier) @name
  (union_declaration)) @definition.struct

(variable_declaration
  (identifier) @name
  (opaque_declaration)) @definition.struct

(variable_declaration
  (identifier) @name
  (error_set_declaration)) @definition.enum
