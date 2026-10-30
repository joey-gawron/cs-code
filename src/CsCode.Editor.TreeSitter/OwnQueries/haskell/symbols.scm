; CsCode outline (tags format): functions (each equation), type signatures are left out; data types, newtypes, synonyms, classes, instances.
(function
  name: (_) @name) @definition.function

(data_type
  name: (_) @name) @definition.class

(newtype
  name: (_) @name) @definition.class

(type_synonym
  name: (_) @name) @definition.type

(type_family
  name: (_) @name) @definition.type

(class
  name: (_) @name) @definition.interface

(instance
  name: (_) @name) @definition.class

; a top-level value without arguments (main = ...)
(declarations
  (bind
    name: (_) @name) @definition.function)
