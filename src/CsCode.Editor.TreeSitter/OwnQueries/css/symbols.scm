; CsCode outline (tags format): rule sets named by their selectors, keyframes, and the media/supports blocks that contain rules.
(rule_set
  (selectors) @name) @definition.class

(keyframes_statement
  (keyframes_name) @name) @definition.function

(media_statement
  .
  (_) @name) @definition.module
