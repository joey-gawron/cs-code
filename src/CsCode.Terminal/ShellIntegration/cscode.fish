# CsCode shell integration for fish: OSC 633 marks around the prompt (A, B), the command line (E) and its output (C), its exit code (D)
# and the working directory (P;Cwd). CsCode starts fish as `fish --init-command 'source <this file>'`, which runs after fish read its own
# configuration (config.fish, conf.d): the user's files are read as usual and never changed.
if not status is-interactive; or set -q __cscode_installed
    exit 0
end
set -g __cscode_installed 1
set -g __cscode_nonce "$CSCODE_NONCE"
set -e CSCODE_NONCE
set -e CSCODE_INJECTION
set -g __cscode_executing 0

# A value in an OSC 633 sequence: backslashes doubled, ';' and the usual control characters as \xHH (any other control character is
# dropped), so it can never end the sequence early. Builtins only: no process per prompt.
function __cscode_escape
    set -l s (string replace -a -- '\\' '\\\\' "$argv[1]" | string collect -N)
    set s (string replace -a -- ';' '\\x3b' "$s" | string collect -N)
    for pair in \n:0a \r:0d \t:09 \e:1b \a:07 \b:08 \f:0c \v:0b
        set -l parts (string split -m1 : -- $pair)
        set s (string replace -a -- $parts[1] "\\x$parts[2]" "$s" | string collect -N)
    end
    string replace -ra -- '[\x00-\x1f\x7f]' '' "$s" | string collect -N
end

function __cscode_set_status
    return $argv[1]
end

# the command line as typed (fish passes it to the event): E with it, then C (its output starts here)
function __cscode_preexec --on-event fish_preexec
    set -g __cscode_executing 1
    printf '\e]633;E;%s;%s\a\e]633;C\a' (__cscode_escape "$argv[1]") "$__cscode_nonce"
end

# $status is the command's own exit status inside the fish_postexec event
function __cscode_postexec --on-event fish_postexec
    set -l code $status
    if test "$__cscode_executing" = 1
        printf '\e]633;D;%s\a' $code
        set -g __cscode_executing 0
    end
end

# The prompt: the directory, then A, the user's prompt, B. fish_prompt is wrapped (the user's, or fish's default, keeps drawing it).
if functions -q fish_prompt
    functions -c fish_prompt __cscode_user_fish_prompt
else
    function __cscode_user_fish_prompt
        printf '%s> ' (prompt_pwd)
    end
end

function fish_prompt
    set -l code $status
    printf '\e]633;P;Cwd=%s\a' (__cscode_escape "$PWD")
    printf '\e]633;A\a'
    # the user's prompt sees the command's $status
    __cscode_set_status $code
    __cscode_user_fish_prompt
    printf '\e]633;B\a'
end
