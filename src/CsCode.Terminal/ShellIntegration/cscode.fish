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
# Directories CsCode put first on PATH (the selected Python environment's bin), first again after fish's own configuration ran.
if set -q CSCODE_PATH_PREPEND
    set -gx PATH (string split : -- $CSCODE_PATH_PREPEND) $PATH
    set -e CSCODE_PATH_PREPEND
end
set -g __cscode_executing 0

# A value in an OSC 633 sequence: backslashes doubled, ';' and the usual control characters as \xHH (any other control character is
# dropped), so it can never end the sequence early. Builtins only: no process per prompt.
function __cscode_escape
    # `string replace` prints its result and a newline; `string collect` takes the result back without that newline
    set -l s (string replace -a -- '\\' '\\\\' "$argv[1]" | string collect)
    set s (string replace -a -- ';' '\\x3b' "$s" | string collect)
    for pair in \n:0a \r:0d \t:09 \e:1b \a:07 \b:08 \f:0c \v:0b
        set -l parts (string split -m1 : -- $pair)
        set s (string replace -a -- $parts[1] "\\x$parts[2]" "$s" | string collect)
    end
    string replace -ra -- '[\x00-\x1f\x7f]' '' "$s" | string collect
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

# The directory, straight to the terminal before each prompt is drawn (inside the prompt text fish would count it as visible width).
function __cscode_prompt_event --on-event fish_prompt
    printf '\e]633;P;Cwd=%s\a' (__cscode_escape "$PWD")
end

# fish 4 marks its prompt itself (OSC 133 A and B around it). Older fish does not: its fish_prompt (the user's, or fish's default) is
# wrapped in A and B.
if test (string split -m1 . -- "$FISH_VERSION")[1] -lt 4
    if functions -q fish_prompt
        functions -c fish_prompt __cscode_user_fish_prompt
    else
        function __cscode_user_fish_prompt
            printf '%s> ' (prompt_pwd)
        end
    end

    function fish_prompt
        set -l code $status
        printf '\e]633;A\a'
        # the user's prompt sees the command's $status
        __cscode_set_status $code
        __cscode_user_fish_prompt
        printf '\e]633;B\a'
    end
end
