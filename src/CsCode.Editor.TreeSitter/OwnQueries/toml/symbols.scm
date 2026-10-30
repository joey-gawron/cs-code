; CsCode outline (tags format): tables, arrays of tables and their keys ([dependencies] > serde).
(table
  .
  [(bare_key) (dotted_key) (quoted_key)] @name) @definition.table

(table_array_element
  .
  [(bare_key) (dotted_key) (quoted_key)] @name) @definition.table

(pair
  .
  [(bare_key) (dotted_key) (quoted_key)] @name) @definition.key
